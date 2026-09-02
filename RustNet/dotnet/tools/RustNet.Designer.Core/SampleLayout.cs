namespace RustNet.Designer;

/// <summary>
/// The layout a Designer session starts from, and the fixture every headless
/// path uses when given no file.
/// </summary>
/// <remarks>
/// It lived as a const on the WPF <c>App</c> class, which made a string of
/// RustNet.UI XML — data, and the one input the assistant, the exporter and
/// the self-test all share — reachable only from Windows. It is here so the
/// front-end owns none of it.
/// </remarks>
public static class SampleLayout
{
    public const string Xml =
        "<window width=\"160\" height=\"128\" bg=\"0000\" pad=\"4\" gap=\"4\">\n" +
        "  <label id=\"title\" text=\"Thermostat\" scale=\"2\" fg=\"07FF\"/>\n" +
        "  <slider id=\"setpoint\" min=\"10\" max=\"30\" value=\"21\" fg=\"F800\"/>\n" +
        "  <checkbox id=\"eco\" text=\"Eco mode\" checked=\"true\"/>\n" +
        "  <listbox id=\"zones\" items=\"Kitchen;Garage;Attic\" selected=\"0\"/>\n" +
        "  <button id=\"apply\" text=\"Apply\" bg=\"4208\"/>\n" +
        "</window>\n";
}
