using Rustzor.Interop;

namespace KamarGelap.Model;

/// <summary>
/// The sample picture: Merapi at dawn seen from Kaliurang, painted by the browser
/// module (nothing to download, nothing licensed), with the edits a photographer
/// would stack on it: a warmer sky, a vignette, a masked lift for the terraces
/// and a caption.
/// </summary>
public static class Seed
{
    public const int W = 2000;
    public const int H = 1500;

    public static void Open(Editor ed)
    {
        Js.Send("kg.newDoc", Js.Args("w", W, "h", H));
        var d = new Doc { Name = "Merapi dari Kaliurang", W = W, H = H };

        var photo = new Layer { Id = d.NewId(), Name = "Merapi, 05.40", Kind = LayerKind.Pixel };
        d.Layers.Add(photo);
        Js.Send("kg.layer", Js.Args("id", photo.Id));
        Js.Send("kg.sample", Js.Args("id", photo.Id, "kind", "merapi"));

        var warm = new Layer
        {
            Id = d.NewId(), Name = "Warmer dawn", Kind = LayerKind.Adjust, Adjust = AdjustKind.HueSaturation,
            P = new double[] { -4, 24, 0, 0, 0, 0 }, HasMask = true,
        };
        d.Layers.Add(warm);
        Js.Send("kg.layer", Js.Args("id", warm.Id));
        Js.Send("kg.mask", Js.Args("id", warm.Id, "mode", "reveal"));

        var lift = new Layer
        {
            Id = d.NewId(), Name = "Lift the terraces", Kind = LayerKind.Adjust, Adjust = AdjustKind.Levels,
            P = new double[] { 6, 228, 1.32, 0, 255, 0 }, HasMask = true,
        };
        d.Layers.Add(lift);
        Js.Send("kg.layer", Js.Args("id", lift.Id));
        Js.Send("kg.mask", Js.Args("id", lift.Id, "mode", "gradient"));

        var vignette = new Layer { Id = d.NewId(), Name = "Vignette", Kind = LayerKind.Pixel, Blend = "multiply", Opacity = 72 };
        d.Layers.Add(vignette);
        Js.Send("kg.layer", Js.Args("id", vignette.Id));
        Js.Send("kg.sample", Js.Args("id", vignette.Id, "kind", "vignette"));

        var caption = new Layer
        {
            Id = d.NewId(), Name = "Caption", Kind = LayerKind.Text, Text = "Kaliurang, 05.42", Font = "DM Serif Display", Italic = true,
            Size = 76, Color = "#fff1dd", TX = 112, TY = 1318, Opacity = 88,
        };
        d.Layers.Add(caption);
        Js.Send("kg.layer", Js.Args("id", caption.Id));

        d.Active = photo.Id;
        ed.Load(d, "Opened Merapi dari Kaliurang");
        _ = RenderCaption(ed, caption);
    }

    // The font arrives a moment later; the caption is drawn then and becomes part of the first step.
    private static async Task RenderCaption(Editor ed, Layer caption)
    {
        await ed.RenderText(caption);
        ed.RefreshFirstStep();
    }
}
