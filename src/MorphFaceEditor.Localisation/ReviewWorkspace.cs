using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MorphFaceEditor.Localisation;

public sealed class ReviewWorkspace
{
    private CatalogueData _catalogue;
    private byte[] _fingerprint;
    public string Path { get; private set; }
    public string BackupPath => Path + ".localiser.bak";
    public IReadOnlyList<ReviewEntry> Entries { get; }
    public bool IsDirty => Entries.Any(entry => entry.IsDirty);

    private ReviewWorkspace(string path, byte[] bytes, CatalogueData catalogue)
    {
        Path = path; _fingerprint = SHA256.HashData(bytes); _catalogue = catalogue;
        Entries = catalogue.Items.Select(item => new ReviewEntry(item, catalogue.Annotations.GetValueOrDefault(item.Key))).ToArray();
    }

    public static ReviewWorkspace Open(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (new FileInfo(fullPath).Length > 16_000_000) throw new FormatException("The catalogue is too large.");
        var bytes = File.ReadAllBytes(fullPath);
        using var stream = new MemoryStream(bytes);
        var data = StringCatalogue.Read(stream);
        if (data.Strings.Count == 0) throw new FormatException("The English master must contain strings.");
        foreach (var key in data.Annotations.Keys)
            if (!data.Strings.ContainsKey(key)) throw new FormatException($"Annotation has no matching master string: {key}");
        StringCatalogue.ValidateOverlay(data.Strings, data.Strings);
        return new ReviewWorkspace(fullPath, bytes, data);
    }

    public void Save() { if (IsDirty) SaveAs(Path); }
    public void SaveAs(string path)
    {
        var write = PrepareWrite(System.IO.Path.GetFullPath(path));
        CatalogueFileWriter.Write(write); AcceptSaved(write);
    }

    internal CatalogueWrite PrepareWrite(string path)
    {
        foreach (var entry in Entries) StringCatalogue.ValidateValue(entry.Key, entry.SavedText, entry.Text);
        var document = new XDocument(_catalogue.Document);
        foreach (var comment in document.Root!.Nodes().OfType<XComment>().Where(StringCatalogue.IsAnnotation).ToArray()) comment.Remove();
        var byKey = Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        foreach (var element in document.Root.Elements())
        {
            var entry = byKey[(string)element.Attribute(StringCatalogue.Xaml + "Key")!];
            if (element.Value != entry.Text) element.Value = entry.Text;
            var annotation = entry.Annotation;
            if (annotation.ReviewedHash is not null || annotation.DoNotTranslate || annotation.Note.Length > 0)
                element.AddBeforeSelf(StringCatalogue.AnnotationComment(annotation));
        }
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = document.Declaration is null, NewLineHandling = NewLineHandling.None }))
            document.Save(writer);
        var bytes = stream.ToArray();
        using var validation = new MemoryStream(bytes); _ = StringCatalogue.Read(validation);
        return new CatalogueWrite(path, bytes, string.Equals(path, Path, StringComparison.OrdinalIgnoreCase) ? _fingerprint : CatalogueFileWriter.Fingerprint(path));
    }

    internal void AcceptSaved(CatalogueWrite write)
    {
        using var stream = new MemoryStream(write.Bytes); _catalogue = StringCatalogue.Read(stream);
        Path = write.Path; _fingerprint = SHA256.HashData(write.Bytes);
        foreach (var entry in Entries) entry.AcceptSaved();
    }
}
