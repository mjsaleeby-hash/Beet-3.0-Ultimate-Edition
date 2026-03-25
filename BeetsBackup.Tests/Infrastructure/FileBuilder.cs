namespace BeetsBackup.Tests.Infrastructure;

public class FileBuilder
{
    private readonly string _root;
    private readonly List<Action> _actions = new();

    private FileBuilder(string root) => _root = root;

    public static FileBuilder In(string root) => new(root);

    public FileBuilder File(string relativePath, int sizeBytes = 0, bool hidden = false)
    {
        _actions.Add(() =>
        {
            var fullPath = Path.Combine(_root, relativePath);
            var dir = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(dir);
            var content = new byte[sizeBytes];
            if (sizeBytes > 0)
                new Random(42).NextBytes(content);
            System.IO.File.WriteAllBytes(fullPath, content);
            if (hidden)
                System.IO.File.SetAttributes(fullPath, System.IO.File.GetAttributes(fullPath) | FileAttributes.Hidden);
        });
        return this;
    }

    public FileBuilder Dir(string relativePath, bool hidden = false)
    {
        _actions.Add(() =>
        {
            var fullPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(fullPath);
            if (hidden)
                System.IO.File.SetAttributes(fullPath, System.IO.File.GetAttributes(fullPath) | FileAttributes.Hidden);
        });
        return this;
    }

    public void Build()
    {
        foreach (var action in _actions)
            action();
    }
}
