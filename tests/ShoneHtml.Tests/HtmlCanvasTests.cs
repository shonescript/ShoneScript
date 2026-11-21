using AngleSharp.Dom;

namespace ShoneHtml.Tests;

public sealed class HtmlCanvasTests
{
    [Fact]
    public void DrawsAndClearsClippedRectanglesWithSavedColorAndAlpha()
    {
        var errors = new List<HtmlScriptError>();
        using var page = HtmlPage.Parse("""
            <canvas id=c width=4 height=3>Fallback</canvas>
            <script>
              const c = document.getElementById('c');
              const ctx = c.getContext('2d');
              ctx.fillStyle = 'red';
              ctx.fillRect(-1, -1, 4, 3);
              ctx.save();
              ctx.fillStyle = '#0000ff';
              ctx.globalAlpha = 0.5;
              ctx.fillRect(1, 0, 3, 1);
              ctx.restore();
              ctx.fillStyle = 'invalid';
              ctx.fillRect(4, 3, -1, -1);
              ctx.clearRect(1, 1, 2, 2);
            </script>
            """, new() { OnScriptError = errors.Add });
        Assert.Empty(errors);
        var pixels = page.GetCanvasPixels(page.Document.GetElementById("c")!)!;
        Assert.Equal((4, 3), (pixels.Width, pixels.Height));
        Assert.Equal(new byte[] {
            255, 0, 0, 255, 128, 0, 128, 255, 128, 0, 128, 255, 0, 0, 255, 128,
            255, 0, 0, 255,   0, 0,   0,   0,   0, 0,   0,   0, 0, 0,   0,   0,
              0, 0, 0,   0,   0, 0,   0,   0,   0, 0,   0,   0, 255, 0, 0, 255,
        }, pixels.Rgba);
    }

    [Fact]
    public void ReadsAndWritesIndependentClampedImageDataWithSignedCoordinatesAndDirtyRectangles()
    {
        var errors = new List<HtmlScriptError>();
        using var page = HtmlPage.Parse("""
            <canvas id=c width=2 height=2></canvas>
            <script>
              const ctx = document.getElementById('c').getContext('2d');
              const image = ctx.createImageData(3, 2);
              if (!(image.data instanceof Uint8ClampedArray) || image.data.length !== 24 || image.colorSpace !== 'srgb')
                throw new Error('wrong ImageData');
              image.data.set([300, -1, 32, 64, 8, 16, 32, 255], 4);
              ctx.globalAlpha = 0;
              ctx.putImageData(image, -1, 0, 1, 0, 2, 1);
              image.data[4] = 0;
              const result = ctx.getImageData(2, 1, -3, -1);
              const empty = ctx.createImageData(result);
              if (result.width !== 3 || result.height !== 1 || empty.data[4] !== 0) throw new Error('wrong dimensions');
              document.title = result.data.join(',');
            </script>
            """, new() { OnScriptError = errors.Add });
        Assert.Empty(errors);
        Assert.Equal("0,0,0,0,255,0,32,64,8,16,32,255", page.Title);
    }

    [Fact]
    public void ReflectsDimensionsAndResetsPixelsAndStateEvenOnEqualAssignment()
    {
        var errors = new List<HtmlScriptError>();
        using var page = HtmlPage.Parse("""
            <canvas id=c></canvas>
            <script>
              const c = document.getElementById('c');
              const ctx = c.getContext('2d', {willReadFrequently: true, colorSpace: 'display-p3'});
              const attributes = ctx.getContextAttributes();
              if (c.width !== 300 || c.height !== 150 || ctx !== c.getContext('2d') || ctx.canvas !== c ||
                  c.getContext('webgl') !== null || attributes.colorSpace !== 'srgb' || attributes.alpha !== true ||
                  attributes.willReadFrequently !== true) throw new Error('wrong canvas or context attributes');
              c.width = 2;
              c.height = 1;
              ctx.fillStyle = 'red';
              ctx.save();
              ctx.fillRect(0, 0, 2, 1);
              c.width = c.width;
              ctx.restore();
              if (ctx.fillStyle !== '#000000' || ctx.getImageData(0, 0, 1, 1).data[3] !== 0) throw new Error('property reset failed');
              ctx.fillRect(0, 0, 1, 1);
              c.setAttribute('HEIGHT', '1');
              if (ctx.getImageData(0, 0, 1, 1).data[3] !== 0) throw new Error('attribute reset failed');
              c.removeAttribute('height');
              if (c.height !== 150) throw new Error('height default failed');
              c.height = 1;
              ctx.fillStyle = 'rgba(255, 0, 0, 0.5)';
              ctx.fillRect(1, 0, 1, 1);
            </script>
            """, new() { OnScriptError = errors.Add });
        Assert.Empty(errors);
        var canvas = page.Document.GetElementById("c")!;
        Assert.Equal("2", canvas.GetAttribute("width"));
        Assert.Equal("1", canvas.GetAttribute("height"));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255, 0, 0, 128 }, page.GetCanvasPixels(canvas)?.Rgba);
    }

    [Fact]
    public void ContextResetClearsDrawingStatePixelsAndSavedStack()
    {
        var errors = new List<HtmlScriptError>();
        using var page = HtmlPage.Parse("""
            <canvas id=c width=2 height=1></canvas>
            <script>
              const ctx = document.getElementById('c').getContext('2d');
              ctx.fillStyle = 'red';
              ctx.save();
              ctx.globalAlpha = 0.25;
              ctx.fillRect(0, 0, 2, 1);
              ctx.reset();
              ctx.restore();
              if (ctx.fillStyle !== '#000000' || ctx.globalAlpha !== 1 || ctx.getImageData(0, 0, 2, 1).data[3] !== 0)
                throw new Error('reset did not restore the default state');
            </script>
            """, new() { OnScriptError = errors.Add });
        Assert.Empty(errors);
        Assert.Null(page.GetCanvasPixels(page.Document.GetElementById("c")!));
    }

    [Fact]
    public void RetainedImageDataBuffersHaveACombinedPageBudget()
    {
        var errors = new List<HtmlScriptError>();
        using var page = HtmlPage.Parse("""
            <canvas id=c></canvas>
            <script>
              const ctx = document.getElementById('c').getContext('2d');
              const retained = [];
              for (let i = 0; i < 8; i++) retained.push(ctx.createImageData(1024, 1024).data.buffer);
              try { ctx.createImageData(1, 1); } catch (error) { document.title = error.name; }
            </script>
            """, new() { OnScriptError = errors.Add });
        Assert.Empty(errors);
        Assert.Equal("RangeError", page.Title);
    }

    [Fact]
    public void ZeroAndOversizedCanvasAllocationsAreBounded()
    {
        var errors = new List<HtmlScriptError>();
        using var page = HtmlPage.Parse("""
            <canvas id=zero width=0 height=4></canvas><canvas id=large width=100000 height=100000></canvas>
            <script>
              ['zero', 'large'].forEach(id => {
                const ctx = document.getElementById(id).getContext('2d');
                ctx.fillRect(0, 0, 100000, 100000);
                ctx.putImageData(ctx.createImageData(1, 1), 0, 0);
                if (ctx.getImageData(0, 0, 1, 1).data[3] !== 0) throw new Error('unexpected draw');
                try { ctx.getImageData(0, 0, 100000, 100000); } catch (error) { document.title += error.name; }
                try { ctx.createImageData(0, 1); } catch (error) { document.title += error.name; }
              });
            </script>
            """, new() { OnScriptError = errors.Add });
        Assert.Empty(errors);
        Assert.Equal("RangeErrorIndexSizeErrorRangeErrorIndexSizeError", page.Title);
        Assert.Null(page.GetCanvasPixels(page.Document.GetElementById("zero")!));
        Assert.Null(page.GetCanvasPixels(page.Document.GetElementById("large")!));
    }
}
