using System.Collections.Concurrent;
using System.Threading.Channels;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace ShoneHtml;

internal sealed record HtmlRendererDocument(HtmlBrowserOptions Options, Uri Origin, bool HasCsp,
    Func<HtmlRendererRequest, CancellationToken, Task<HtmlResource>> LoadAsync,
    Action<HtmlRendererNavigation> Navigate, Action Abort);

internal static class HtmlRendererWorker
{
    internal static async Task<int> RunAsync(Stream input, Stream output, string[] args)
    {
        if (args is not ["--renderer"]) return 2;
        using var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        var wire = new HtmlRendererProtocol(input, output);
        var commands = Channel.CreateBounded<HtmlRendererProtocol.Frame>(4);
        var requests = new ConcurrentDictionary<int, TaskCompletionSource<HtmlRendererResource>>();
        var requestSequence = 0;
        using var requestOrder = new SemaphoreSlim(1);
        long document = 0;
        try
        {
            await wire.WriteAsync(HtmlRendererMessage.Hello, 0, 0, token);
            var open = await wire.ReadAsync(token);
            if (open.Type != HtmlRendererMessage.Open || open.Document <= 0 || open.Correlation != 1)
                throw new InvalidDataException("Expected a renderer document.");
            document = open.Document;
            var config = open.Decode(HtmlRendererJson.Default.HtmlRendererOpen);
            var resource = config.Document.ToResource();
            using var context = new HtmlBrowser.BrowserContext();
            using var readerLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var reader = ReadLoop(readerLifetime.Token);
            HtmlPage? page = null;
            var nextId = 0;
            var ids = new System.Runtime.CompilerServices.ConditionalWeakTable<IElement, ElementId>();
            Dictionary<int, IElement> targets = [];
            long version = -1;
            string? error = null;
            HtmlRendererNavigation? navigation = null;
            try
            {
                await OnOwner(async () =>
                {
                    var options = new HtmlBrowserOptions
                    {
                        MaximumDocumentNodes = Math.Clamp(config.MaximumDocumentNodes, 1, 20_000),
                        MaximumDocumentDepth = Math.Clamp(config.MaximumDocumentDepth, 1, 96),
                        ScriptOptions = new()
                        {
                            MaximumLoopIterations = Math.Clamp(config.MaximumLoopIterations, 0, 1_000_000),
                            OnScriptError = e => error = e.Source[..Math.Min(e.Source.Length, 512)]
                        }
                    };
                    var environment = new HtmlRendererDocument(options, resource.Address,
                        resource.Headers.ContainsKey("Content-Security-Policy"), LoadAsync,
                        intent => navigation ??= intent, lifetime.Cancel);
                    page = await HtmlPage.LoadRendererDocumentAsync(environment, context, resource, token);
                    if (resource.IsSuccess) await page.LoadScriptsAsync(options.ScriptOptions);
                    await Reply(1);
                });
                await foreach (var command in commands.Reader.ReadAllAsync(token))
                {
                    await OnOwner(async () =>
                    {
                        if (command.Type == HtmlRendererMessage.Input)
                        {
                            var evt = command.Decode(HtmlRendererJson.Default.HtmlRendererInput);
                            if (!Enum.IsDefined(evt.Kind) || evt.Text?.Length > 32768) throw new InvalidDataException("Invalid renderer input.");
                            if (evt.Kind == HtmlRendererInputKind.Address)
                            {
                                var before = page!.Address!;
                                if (Uri.TryCreate(evt.Text, UriKind.Absolute, out var next) && HtmlResources.IsHttpAddress(next) && HtmlResources.SameOrigin(resource.Address, next))
                                {
                                    page.Address = next;
                                    page.NotifyHistory(before, evt.Index == 1);
                                }
                            }
                            else if (evt.Kind == HtmlRendererInputKind.Focus && evt.Id == 0) page!.SetFocusedElement(null);
                            else if (targets.TryGetValue(evt.Id, out var target) && page!.Document.Contains(target))
                                await ApplyInput(page, target, evt);
                        }
                        await Reply(command.Correlation);
                    });
                }
            }
            finally
            {
                readerLifetime.Cancel();
                lifetime.Cancel();
                await OnOwner(() => { page?.Dispose(); return Task.CompletedTask; });
                try { await reader; } catch (OperationCanceledException) { }
            }
            return 0;

            async Task Reply(int correlation)
            {
                HtmlRendererSnapshot? snapshot = null;
                if (page!.Version != version)
                {
                    var visible = new Dictionary<int, IElement>();
                    snapshot = HtmlRendererProjection.Capture(page, element =>
                    {
                        var id = ids.GetValue(element, _ => new ElementId(checked(++nextId))).Value;
                        visible.Add(id, element);
                        return id;
                    });
                    targets = visible;
                    version = page.Version;
                }
                var focused = page.FocusedElement is { } focus && ids.TryGetValue(focus, out var id) && targets.ContainsKey(id.Value) ? id.Value : 0;
                var result = new HtmlRendererResult(snapshot, navigation, error, focused);
                navigation = null;
                error = null;
                await wire.WriteAsync(HtmlRendererMessage.Result, document, correlation, result, HtmlRendererJson.Default.HtmlRendererResult, token);
            }

            Task OnOwner(Func<Task> callback)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                context.Post(async _ =>
                {
                    try { await callback(); done.SetResult(); }
                    catch (Exception e) { done.SetException(e); }
                }, null);
                return done.Task;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            return 1;
        }

        async Task<HtmlResource> LoadAsync(HtmlRendererRequest request, CancellationToken requestToken)
        {
            requestToken.ThrowIfCancellationRequested();
            if (requests.Count >= 64) { lifetime.Cancel(); throw new HttpRequestException("Renderer request queue exceeded."); }
            var id = 0;
            var reply = new TaskCompletionSource<HtmlRendererResource>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await requestOrder.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    id = checked(++requestSequence);
                    if (!requests.TryAdd(id, reply)) throw new InvalidOperationException("Invalid renderer request sequence.");
                    await wire.WriteAsync(HtmlRendererMessage.Request, document, id, request, HtmlRendererJson.Default.HtmlRendererRequest, token).ConfigureAwait(false);
                }
                finally { requestOrder.Release(); }
                var resource = await reply.Task.WaitAsync(token).ConfigureAwait(false);
                requestToken.ThrowIfCancellationRequested();
                return resource.ToResource();
            }
            finally { requests.TryRemove(id, out _); }
        }

        async Task ReadLoop(CancellationToken readToken)
        {
            var lastCommand = 1;
            try
            {
                while (true)
                {
                    var frame = await wire.ReadAsync(readToken).ConfigureAwait(false);
                    if (frame.Document != document) throw new InvalidDataException("Wrong renderer document.");
                    if (frame.Type == HtmlRendererMessage.Resource && requests.TryGetValue(frame.Correlation, out var reply) &&
                        reply.TrySetResult(frame.Decode(HtmlRendererJson.Default.HtmlRendererResource))) continue;
                    if (frame.Type is not (HtmlRendererMessage.Input or HtmlRendererMessage.Poll) || frame.Correlation != ++lastCommand || !commands.Writer.TryWrite(frame))
                        throw new InvalidDataException("Invalid renderer command.");
                }
            }
            catch (Exception e)
            {
                commands.Writer.TryComplete(e);
                lifetime.Cancel();
            }
        }
    }

    private sealed record ElementId(int Value);

    private static Task ApplyInput(HtmlPage page, IElement target, HtmlRendererInput input)
    {
        switch (input.Kind)
        {
            case HtmlRendererInputKind.Click: return page.ActivateAsync(target);
            case HtmlRendererInputKind.Focus: page.SetFocusedElement(target); break;
            case HtmlRendererInputKind.Submit when target is IHtmlFormElement: return page.SubmitFormAsync(target);
            case HtmlRendererInputKind.Text when !HtmlForms.IsDisabled(target) && !target.HasAttribute("readonly"):
                if (target is IHtmlInputElement field && field.Type is not ("file" or "checkbox" or "radio" or "hidden" or "button" or "submit" or "reset")) field.Value = input.Text ?? "";
                else if (target is IHtmlTextAreaElement area) area.Value = input.Text ?? "";
                else break;
                page.MarkDirty();
                page.DispatchEvent(target, "input");
                page.PublishChanges();
                break;
            case HtmlRendererInputKind.Change: page.DispatchEvent(target, "change"); break;
            case HtmlRendererInputKind.Select when target is IHtmlSelectElement select && !HtmlForms.IsDisabled(select):
                var options = select.Options.ToArray();
                if (input.Index >= 0 && input.Index < options.Length && !HtmlForms.IsDisabled(options[input.Index]))
                {
                    foreach (var option in options) option.IsSelected = ReferenceEquals(option, options[input.Index]);
                    page.FormControlChanged(select);
                }
                break;
        }
        return Task.CompletedTask;
    }
}
