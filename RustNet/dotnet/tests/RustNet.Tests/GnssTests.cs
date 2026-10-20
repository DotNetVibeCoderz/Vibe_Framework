using RustNet.Sensors.Gnss;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// The NMEA decoder is pure managed code, so it is validated on the host;
/// LibApp runs the same sentences through it on the virtual device.
/// </summary>
public class GnssTests
{
    // Real-world shaped sentences with correct checksums.
    private const string Gga = "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";
    private const string Rmc = "$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A";
    private const string Gsa = "$GPGSA,A,3,04,05,,09,12,,,24,,,,,2.5,1.3,2.1*39";
    private const string Vtg = "$GPVTG,054.7,T,034.4,M,005.5,N,010.2,K*48";
    private const string Gsv1 = "$GPGSV,2,1,08,01,40,083,46,02,17,308,41,12,07,344,39,14,22,228,45*75";
    private const string Gsv2 = "$GPGSV,2,2,08,15,10,050,,17,55,120,38,19,05,220,30,24,70,010,48*7D";

    [Fact]
    public void ChecksumMatchesTheSentences()
    {
        Assert.Equal(0x47, NmeaParser.Checksum(Gga.Substring(1, Gga.IndexOf('*') - 1)));
        foreach (string s in new[] { Gga, Rmc, Gsa, Vtg, Gsv1, Gsv2 })
        {
            Assert.True(new NmeaParser().ParseSentence(s), s);
        }
    }

    [Fact]
    public void GgaGivesPositionAltitudeAndSatellites()
    {
        var p = new NmeaParser();
        GnssFix? seen = null;
        p.PositionReceived = f => seen = f;
        Assert.True(p.ParseSentence(Gga));
        Assert.NotNull(seen);
        Assert.True(p.Fix.Valid);
        Assert.Equal(48.1173, p.Fix.Latitude, 4);
        Assert.Equal(11.516667, p.Fix.Longitude, 5);
        Assert.Equal(545.4, p.Fix.AltitudeMeters, 3);
        Assert.Equal(8, p.Fix.SatellitesUsed);
        Assert.Equal(0.9, p.Fix.Hdop, 3);
        Assert.Equal("12:35:19", p.Fix.TimeText());
    }

    [Fact]
    public void RmcGivesDateSpeedAndCourse()
    {
        var p = new NmeaParser();
        int velocity = 0;
        p.VelocityReceived = _ => velocity++;
        p.ParseSentence(Rmc);
        Assert.Equal("1994-03-23", p.Fix.DateText());
        Assert.Equal(22.4, p.Fix.SpeedKnots, 3);
        Assert.Equal(22.4 * 1.852, p.Fix.SpeedKmh, 3);
        Assert.Equal(84.4, p.Fix.CourseDegrees, 3);
        Assert.Equal(1, velocity);
    }

    [Fact]
    public void GsaAndVtgFillDopAndVelocity()
    {
        var p = new NmeaParser();
        p.ParseSentence(Gsa);
        p.ParseSentence(Vtg);
        Assert.Equal(3, p.Fix.FixMode);
        Assert.Equal(2.5, p.Fix.Pdop, 3);
        Assert.Equal(2.1, p.Fix.Vdop, 3);
        Assert.Equal(10.2, p.Fix.SpeedKmh, 3);
        Assert.Equal(54.7, p.Fix.CourseDegrees, 3);
    }

    [Fact]
    public void GsvGroupCompletesIntoSatellitesInView()
    {
        var p = new NmeaParser();
        List<SatelliteInfo>? sats = null;
        p.SatellitesInView = s => sats = s;
        p.ParseSentence(Gsv1);
        Assert.Null(sats); // group not complete yet
        p.ParseSentence(Gsv2);
        Assert.NotNull(sats);
        Assert.Equal(8, sats!.Count);
        Assert.Equal(1, sats[0].Prn);
        Assert.Equal(46, sats[0].Snr);
        Assert.Equal(-1, sats[4].Snr); // PRN 15: not tracked
        Assert.Equal("GP", sats[7].System);
    }

    [Fact]
    public void StreamingFeedSurvivesArbitraryChunkingAndNoise()
    {
        var p = new NmeaParser();
        string stream = "garbage\r\n" + Gga + "\r\n" + Rmc.Replace("*6A", "*00") + "\r\n$PMTK001,314,3*36\r\n" + Rmc + "\n";
        string? other = null;
        p.OtherSentence = s => other = s;
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(stream);
        for (int i = 0; i < bytes.Length; i += 7)
        {
            p.Feed(bytes.AsSpan(i, Math.Min(7, bytes.Length - i)).ToArray());
        }
        Assert.Equal(2, p.SentencesParsed);
        Assert.Equal(1, p.ChecksumErrors);
        Assert.StartsWith("$PMTK001", other);
        Assert.Equal("1994-03-23", p.Fix.DateText());
    }

    [Fact]
    public void NoFixClearsValid()
    {
        var p = new NmeaParser();
        p.ParseSentence(Gga);
        p.ParseSentence("$GPGGA,123520,,,,,0,00,,,M,,M,,");
        Assert.False(p.Fix.Valid);
        Assert.Equal(-33.8688, NmeaParser.Coordinate("3352.128", "S"), 4);
    }
}
