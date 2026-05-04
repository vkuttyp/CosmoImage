using System;
using System.Linq;
using System.Text;
using CosmoImage.Operations.Metadata;
using Xunit;

namespace CosmoImage.Tests;

public class Round86Tests
{
    private const string Dc = VipsXmpProfile.Namespaces.Dc;
    private const string Xmp = VipsXmpProfile.Namespaces.Xmp;
    private const string Photoshop = VipsXmpProfile.Namespaces.Photoshop;

    // ---- Empty profile ----

    [Fact]
    public void NewProfile_HasEmptyDocument_BuildsValidXml()
    {
        var p = new VipsXmpProfile();
        var bytes = p.ToBytes();
        Assert.NotEmpty(bytes);
        var back = VipsXmpProfile.TryParse(bytes);
        Assert.NotNull(back);
    }

    // ---- Simple property ----

    [Fact]
    public void SimpleProperty_RoundTrip()
    {
        var p = new VipsXmpProfile();
        p.SetProperty(Xmp, "CreateDate", "2026-05-04T12:00:00");
        p.SetProperty(Xmp, "CreatorTool", "CosmoImage");
        var back = VipsXmpProfile.TryParse(p.ToBytes())!;
        Assert.Equal("2026-05-04T12:00:00", back.GetProperty(Xmp, "CreateDate"));
        Assert.Equal("CosmoImage", back.GetProperty(Xmp, "CreatorTool"));
    }

    [Fact]
    public void SimpleProperty_OverwriteReplaces()
    {
        var p = new VipsXmpProfile();
        p.SetProperty(Xmp, "CreatorTool", "First");
        p.SetProperty(Xmp, "CreatorTool", "Second");
        Assert.Equal("Second", p.GetProperty(Xmp, "CreatorTool"));
        var back = VipsXmpProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Second", back.GetProperty(Xmp, "CreatorTool"));
    }

    [Fact]
    public void SimpleProperty_MissingReturnsNull()
    {
        var p = new VipsXmpProfile();
        Assert.Null(p.GetProperty(Xmp, "DoesNotExist"));
    }

    [Fact]
    public void RemoveProperty_DropsValue()
    {
        var p = new VipsXmpProfile();
        p.SetProperty(Xmp, "CreatorTool", "X");
        Assert.True(p.RemoveProperty(Xmp, "CreatorTool"));
        Assert.Null(p.GetProperty(Xmp, "CreatorTool"));
        Assert.False(p.RemoveProperty(Xmp, "CreatorTool"));  // already gone
    }

    // ---- Bag (unordered list) ----

    [Fact]
    public void Bag_RoundTrip()
    {
        var p = new VipsXmpProfile();
        p.SetBag(Dc, "subject", new[] { "landscape", "sunset", "ocean" });
        var back = VipsXmpProfile.TryParse(p.ToBytes())!;
        var keywords = back.GetList(Dc, "subject");
        Assert.Equal(3, keywords.Count);
        Assert.Contains("landscape", keywords);
        Assert.Contains("sunset", keywords);
        Assert.Contains("ocean", keywords);
    }

    [Fact]
    public void Bag_OverwriteReplaces()
    {
        var p = new VipsXmpProfile();
        p.SetBag(Dc, "subject", new[] { "old1", "old2" });
        p.SetBag(Dc, "subject", new[] { "new" });
        var keywords = p.GetList(Dc, "subject");
        Assert.Single(keywords);
        Assert.Equal("new", keywords[0]);
    }

    // ---- Seq (ordered list) ----

    [Fact]
    public void Seq_PreservesOrder()
    {
        var p = new VipsXmpProfile();
        p.SetSeq(Dc, "creator", new[] { "Alice", "Bob", "Charlie" });
        var back = VipsXmpProfile.TryParse(p.ToBytes())!;
        var creators = back.GetList(Dc, "creator");
        Assert.Equal(new[] { "Alice", "Bob", "Charlie" }, creators);
    }

    // ---- Alt-lang ----

    [Fact]
    public void AltLang_DefaultRoundTrip()
    {
        var p = new VipsXmpProfile();
        p.SetAltLang(Dc, "title", "Sunrise on the Bay");
        var back = VipsXmpProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Sunrise on the Bay", back.GetAltLang(Dc, "title"));
    }

    [Fact]
    public void AltLang_MultipleLanguagesPreserved()
    {
        var p = new VipsXmpProfile();
        p.SetAltLang(Dc, "title", "Hello world", "x-default");
        p.SetAltLang(Dc, "title", "Hola mundo", "es");
        p.SetAltLang(Dc, "title", "こんにちは世界", "ja");
        var back = VipsXmpProfile.TryParse(p.ToBytes())!;
        Assert.Equal("Hello world", back.GetAltLang(Dc, "title", "x-default"));
        Assert.Equal("Hola mundo", back.GetAltLang(Dc, "title", "es"));
        Assert.Equal("こんにちは世界", back.GetAltLang(Dc, "title", "ja"));
    }

    [Fact]
    public void AltLang_SameLanguageReplaces()
    {
        var p = new VipsXmpProfile();
        p.SetAltLang(Dc, "title", "First");
        p.SetAltLang(Dc, "title", "Replacement");
        Assert.Equal("Replacement", p.GetAltLang(Dc, "title"));
    }

    [Fact]
    public void AltLang_MissingLanguageReturnsNull()
    {
        var p = new VipsXmpProfile();
        p.SetAltLang(Dc, "title", "x");
        Assert.Null(p.GetAltLang(Dc, "title", "fr"));
    }

    // ---- Mixed namespaces ----

    [Fact]
    public void MultipleNamespaces_RoundTrip()
    {
        var p = new VipsXmpProfile();
        p.SetProperty(Xmp, "CreateDate", "2026-05-04T12:00:00");
        p.SetAltLang(Dc, "title", "My Photo");
        p.SetBag(Dc, "subject", new[] { "k1", "k2" });
        p.SetProperty(Photoshop, "City", "Paris");

        var back = VipsXmpProfile.TryParse(p.ToBytes())!;
        Assert.Equal("2026-05-04T12:00:00", back.GetProperty(Xmp, "CreateDate"));
        Assert.Equal("My Photo", back.GetAltLang(Dc, "title"));
        Assert.Equal(2, back.GetList(Dc, "subject").Count);
        Assert.Equal("Paris", back.GetProperty(Photoshop, "City"));
    }

    // ---- Real-world XMP fragment ----

    [Fact]
    public void Parses_TypicalXmpPacket()
    {
        // Synthesise an XMP packet similar to what photo apps emit.
        const string sample = @"<?xpacket begin="""" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/"" x:xmptk=""Test"">
  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
    <rdf:Description rdf:about=""""
        xmlns:dc=""http://purl.org/dc/elements/1.1/""
        xmlns:xmp=""http://ns.adobe.com/xap/1.0/"">
      <dc:title>
        <rdf:Alt>
          <rdf:li xml:lang=""x-default"">Sunset</rdf:li>
        </rdf:Alt>
      </dc:title>
      <dc:subject>
        <rdf:Bag>
          <rdf:li>nature</rdf:li>
          <rdf:li>landscape</rdf:li>
        </rdf:Bag>
      </dc:subject>
      <xmp:CreateDate>2026-04-15T18:30:00</xmp:CreateDate>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end=""w""?>";
        var profile = VipsXmpProfile.TryParse(Encoding.UTF8.GetBytes(sample))!;
        Assert.Equal("Sunset", profile.GetAltLang(Dc, "title"));
        var subjects = profile.GetList(Dc, "subject");
        Assert.Equal(2, subjects.Count);
        Assert.Contains("nature", subjects);
        Assert.Equal("2026-04-15T18:30:00", profile.GetProperty(Xmp, "CreateDate"));
    }

    // ---- ContainsProperty ----

    [Fact]
    public void ContainsProperty_DetectsAllShapes()
    {
        var p = new VipsXmpProfile();
        p.SetProperty(Xmp, "Foo", "x");
        p.SetBag(Dc, "subject", new[] { "k" });
        p.SetAltLang(Dc, "title", "t");
        Assert.True(p.ContainsProperty(Xmp, "Foo"));
        Assert.True(p.ContainsProperty(Dc, "subject"));
        Assert.True(p.ContainsProperty(Dc, "title"));
        Assert.False(p.ContainsProperty(Dc, "missing"));
    }

    // ---- Malformed input ----

    [Fact]
    public void TryParse_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(VipsXmpProfile.TryParse(null));
        Assert.Null(VipsXmpProfile.TryParse(Array.Empty<byte>()));
    }

    [Fact]
    public void TryParse_NotXml_ReturnsNull()
    {
        Assert.Null(VipsXmpProfile.TryParse(Encoding.UTF8.GetBytes("not even close to XML")));
    }

    [Fact]
    public void TryParse_TruncatedXml_ReturnsNull()
    {
        Assert.Null(VipsXmpProfile.TryParse(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">")));
    }

    // ---- VipsImage extension methods ----

    [Fact]
    public void VipsImage_RoundTripXmpProfileBlob()
    {
        var profile = new VipsXmpProfile();
        profile.SetAltLang(Dc, "title", "Round-trip test");
        profile.SetBag(Dc, "subject", new[] { "test" });
        profile.SetProperty(Xmp, "CreatorTool", "CosmoImage");

        var image = new VipsImage { Width = 10, Height = 10, Bands = 3 };
        image.SetXmpProfile(profile);

        var retrieved = image.GetXmpProfile();
        Assert.NotNull(retrieved);
        Assert.Equal("Round-trip test", retrieved!.GetAltLang(Dc, "title"));
        Assert.Single(retrieved.GetList(Dc, "subject"));
        Assert.Equal("CosmoImage", retrieved.GetProperty(Xmp, "CreatorTool"));
    }

    [Fact]
    public void VipsImage_NoXmpBlob_GetXmpProfileReturnsNull()
    {
        var image = new VipsImage { Width = 10, Height = 10 };
        Assert.Null(image.GetXmpProfile());
    }

    // ---- Direct DOM access ----

    [Fact]
    public void Document_AccessibleForAdvancedUse()
    {
        var p = new VipsXmpProfile();
        p.SetProperty(Xmp, "CreatorTool", "X");
        // Verify the underlying XDocument is reachable.
        Assert.NotNull(p.Document.Root);
        Assert.Equal("xmpmeta", p.Document.Root!.Name.LocalName);
    }
}
