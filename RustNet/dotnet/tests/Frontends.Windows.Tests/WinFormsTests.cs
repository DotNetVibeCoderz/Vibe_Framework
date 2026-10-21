using RustNet.UI.Frontends;
using RustNet.UI.WinForms;
using Xunit;

namespace RustNet.Frontends.Windows.Tests;

public class WinFormsTests
{
    private static string Parity => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity.xml"));

    [Fact]
    public void BitmapIsTheFramebuffer()
    {
        var session = UiSession.FromXml(Parity);
        session.Render();
        using var bmp = UiControl.ToBitmap(session.Canvas);
        var fb = session.Canvas;
        Assert.Equal(fb.Width, bmp.Width);
        for (int y = 0; y < fb.Height; y += 3)
        {
            for (int x = 0; x < fb.Width; x += 3)
            {
                Assert.Equal(FramebufferCanvas.Rgb565ToArgb(fb.GetPixel(x, y)), (uint)bmp.GetPixel(x, y).ToArgb());
            }
        }
    }

    [Fact]
    public void FormSizesItselfToTheLayout()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new UiForm(Parity, zoom: 3);
                Assert.Equal(new System.Drawing.Size(240 * 3, 300 * 3), form.ClientSize);
                Assert.NotNull(form.View.Frame);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
