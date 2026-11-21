using Aprillz.MewUI;

if (BrowserWorker.TryRun(args))
    return;

const string html = """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <title>ShoneHtml Demo</title>
      <style>
        body { margin: 0; padding: 28px; color: #192a3a; background: #edf4f8; }
        h1 { margin: 0 0 12px; font-size: 32px; }
        p { margin: 0 0 16px; }
        section { background: #ffffff; border: 1px solid #c5d6e2; padding: 20px; margin-bottom: 16px; }
        button { background: #165d70; color: #ffffff; border: 1px solid #165d70; padding: 8px 14px; }
        #counter { margin: 16px 0 0; font-weight: bold; }
      </style>
    </head>
    <body>
      <h1>ShoneHtml</h1>
      <section>
        <button id="increment">Increment count</button>
        <p id="counter">Count: 0</p>
      </section>
      <section>
        <p><strong>Script updates</strong></p>
        <p>Each click runs an inline JavaScript listener. It updates the count, changes the window title, and appends an entry to the list below.</p>
        <div id="log"></div>
      </section>
      <script>
        let count = 0;
        document.getElementById('increment').addEventListener('click', function () {
          count = count + 1;
          document.getElementById('counter').textContent = 'Count: ' + count;
          document.title = 'ShoneHtml Demo | Count: ' + count;
          let entry = document.createElement('p');
          entry.textContent = 'Incremented to ' + count;
          document.getElementById('log').appendChild(entry);
        });
      </script>
    </body>
    </html>
    """;

var application = Application.Create();
if (OperatingSystem.IsWindows())
    application.UseWin32().UseDirect2D();
else if (OperatingSystem.IsLinux())
    application.UseX11().UseMewVGX11();
else if (OperatingSystem.IsMacOS())
    application.UseMacOS().UseMewVGMetal();
else
    throw new PlatformNotSupportedException("The ShoneHtml demo supports Windows, Linux X11, and macOS.");

var trustedScripts = args.Contains("--trusted-scripts", StringComparer.Ordinal);
var address = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
application.BuildMainWindow(() => BrowserWindow.Create(address, html, trustedScripts)).Run();
