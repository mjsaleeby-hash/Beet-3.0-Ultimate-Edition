namespace BeetsBackup.Tests.Infrastructure;

public class FileBuilder
{
    private readonly string _root;
    private readonly List<Action> _actions = new();

    private FileBuilder(string root) => _root = root;

    public static FileBuilder In(string root) => new(root);

    public FileBuilder File(string relativePath, int sizeBytes = 0, bool hidden = false, bool readOnly = false)
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
            if (readOnly)
                System.IO.File.SetAttributes(fullPath, System.IO.File.GetAttributes(fullPath) | FileAttributes.ReadOnly);
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

    /// <summary>
    /// Creates N files with random content in the given subdirectory.
    /// File names: prefix_0000.dat, prefix_0001.dat, etc.
    /// </summary>
    public FileBuilder BulkFiles(string subDir, int count, int sizeBytes, string prefix = "file")
    {
        _actions.Add(() =>
        {
            var dir = Path.Combine(_root, subDir);
            Directory.CreateDirectory(dir);
            var rng = new Random(42);
            for (int i = 0; i < count; i++)
            {
                var content = new byte[sizeBytes];
                rng.NextBytes(content);
                System.IO.File.WriteAllBytes(Path.Combine(dir, $"{prefix}_{i:D4}.dat"), content);
            }
        });
        return this;
    }

    /// <summary>
    /// Creates a single large file with random content.
    /// Uses buffered writes to avoid allocating the full size in memory at once.
    /// </summary>
    public FileBuilder LargeFile(string relativePath, long sizeBytes)
    {
        _actions.Add(() =>
        {
            var fullPath = Path.Combine(_root, relativePath);
            var dir = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(dir);
            var rng = new Random(42);
            var buffer = new byte[64 * 1024]; // 64KB buffer
            using var fs = System.IO.File.Create(fullPath);
            long written = 0;
            while (written < sizeBytes)
            {
                int chunk = (int)Math.Min(buffer.Length, sizeBytes - written);
                rng.NextBytes(buffer.AsSpan(0, chunk));
                fs.Write(buffer, 0, chunk);
                written += chunk;
            }
        });
        return this;
    }

    /// <summary>
    /// Creates a deeply nested directory chain: level0/level1/level2/.../levelN with a file at the bottom.
    /// </summary>
    public FileBuilder DeepNesting(int depth, int fileSize = 10)
    {
        _actions.Add(() =>
        {
            var path = _root;
            for (int i = 0; i < depth; i++)
                path = Path.Combine(path, $"level{i}");
            Directory.CreateDirectory(path);
            var content = new byte[fileSize];
            new Random(42).NextBytes(content);
            System.IO.File.WriteAllBytes(Path.Combine(path, "deep.dat"), content);
        });
        return this;
    }

    /// <summary>
    /// Creates files with unicode and special characters in their names.
    /// </summary>
    public FileBuilder UnicodeFiles(int sizeBytes = 100)
    {
        var names = new[]
        {
            "cafe\u0301.txt",         // café with combining accent
            "\u00e9l\u00e8ve.doc",    // élève
            "\u00fc\u00f6\u00e4.txt", // üöä
            "\u4f60\u597d.txt",       // 你好 (Chinese)
            "\ud83d\ude80 launch.txt",// 🚀 emoji
            "file (copy).txt",        // spaces and parens
            "file [v2].txt",          // brackets
            "file {backup}.txt",      // braces
            "file #1.txt",            // hash
            "file & data.txt",        // ampersand
        };
        foreach (var name in names)
        {
            _actions.Add(() =>
            {
                try
                {
                    var fullPath = Path.Combine(_root, name);
                    var content = new byte[sizeBytes];
                    new Random(42).NextBytes(content);
                    System.IO.File.WriteAllBytes(fullPath, content);
                }
                catch
                {
                    // Some file systems may not support all characters — skip gracefully
                }
            });
        }
        return this;
    }

    /// <summary>
    /// Creates a file with a very long name (within Windows limits).
    /// </summary>
    public FileBuilder LongNameFile(int nameLength = 200, int sizeBytes = 100)
    {
        _actions.Add(() =>
        {
            // Windows MAX_PATH is 260, but the full path matters.
            // Keep the name portion within reasonable limits.
            var safeName = new string('a', Math.Min(nameLength, 200)) + ".dat";
            var fullPath = Path.Combine(_root, safeName);
            var content = new byte[sizeBytes];
            new Random(42).NextBytes(content);
            System.IO.File.WriteAllBytes(fullPath, content);
        });
        return this;
    }

    public void Build()
    {
        foreach (var action in _actions)
            action();
    }
}
