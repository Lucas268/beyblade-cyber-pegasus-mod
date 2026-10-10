using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using MKDS_Course_Modifier.Archive_Format;
using MKDS_Course_Modifier.G3D_Binary_File_Format;
using MKDS_Course_Modifier.IO;

internal static class ExtractAllNarc
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0].Equals("--textures", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 3)
                    throw new ArgumentException("Usage: ExtractAllNarc.exe --textures <input-folder> <output-folder>");
                return ExtractTextures(args[1], args[2]);
            }

            if (args.Length != 2 && args.Length != 4)
            {
                Console.Error.WriteLine("Usage: ExtractAllNarc.exe <input-folder> <output-folder> [--file <archive.narc>]");
                Console.Error.WriteLine("       ExtractAllNarc.exe --textures <input-folder> <output-folder>");
                return 2;
            }

            string inputRoot = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string outputRoot = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(inputRoot))
                throw new DirectoryNotFoundException("Input folder not found: " + inputRoot);
            if (args.Length == 4 && args[2] != "--file")
                throw new ArgumentException("Expected --file before the archive path.");

            Directory.CreateDirectory(outputRoot);
            if (Directory.GetFileSystemEntries(outputRoot).Length != 0)
                throw new IOException("Output folder must be empty: " + outputRoot);

            List<string> archives = FindArchives(inputRoot, outputRoot, args);
            if (archives.Count == 0)
                throw new FileNotFoundException("No NARC archives found.");

            int extracted = 0;
            int failed = 0;
            foreach (string archivePath in archives)
            {
                string relativePath = archivePath.Substring(inputRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destination = Path.GetFullPath(Path.Combine(outputRoot, Path.ChangeExtension(relativePath, null)));
                EnsureWithin(outputRoot, destination);

                try
                {
                    NARC.DirectoryEntry root = NARC.Unpack(archivePath);
                    if (root == null)
                        throw new InvalidDataException("The MKDS parser returned an empty archive tree.");

                    ExtractDirectory(root, destination, ref extracted);
                    Console.WriteLine("Extracted: " + relativePath);
                }
                catch (Exception exception)
                {
                    failed++;
                    if (Directory.Exists(destination))
                        Directory.Delete(destination, true);
                    Console.Error.WriteLine("Failed: " + relativePath + " (" + exception.Message + ")");
                }
            }

            Console.WriteLine("Archives: " + archives.Count + "; files: " + extracted + "; failed: " + failed);
            Console.WriteLine("Output: " + outputRoot);
            return failed == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static List<string> FindArchives(string inputRoot, string outputRoot, string[] args)
    {
        if (args.Length == 4)
        {
            string archivePath = Path.GetFullPath(args[3]);
            if (!archivePath.EndsWith(".narc", StringComparison.OrdinalIgnoreCase) || !IsWithin(inputRoot, archivePath))
                throw new ArgumentException("The selected archive must be a .narc file inside the input folder.");
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("Archive not found: " + archivePath);
            return new List<string> { archivePath };
        }

        List<string> archives = new List<string>();
        foreach (string path in Directory.GetFiles(inputRoot, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".narc", StringComparison.OrdinalIgnoreCase) && !IsWithin(outputRoot, path))
                archives.Add(path);
        }
        archives.Sort(StringComparer.OrdinalIgnoreCase);
        return archives;
    }

    private static int ExtractTextures(string inputPath, string outputPath)
    {
        string inputRoot = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string outputRoot = Path.GetFullPath(outputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(inputRoot))
            throw new DirectoryNotFoundException("Input folder not found: " + inputRoot);

        Directory.CreateDirectory(outputRoot);
        if (Directory.GetFileSystemEntries(outputRoot).Length != 0)
            throw new IOException("Output folder must be empty: " + outputRoot);

        List<string> binaries = new List<string>();
        foreach (string path in Directory.GetFiles(inputRoot, "*.bin", SearchOption.AllDirectories))
        {
            if (!IsWithin(outputRoot, path))
                binaries.Add(path);
        }

        int parsed = 0;
        int exported = 0;
        int failed = 0;
        foreach (string binaryPath in binaries)
        {
            byte[] bytes = new ByteFileInfo(binaryPath).Data;
            int payloadOffset = FindTexturePayload(bytes);
            if (payloadOffset < 0)
                continue;

            string relativePath = binaryPath.Substring(inputRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string relativeFolder = Path.GetDirectoryName(relativePath);
            if (String.IsNullOrEmpty(relativeFolder))
                relativeFolder = String.Empty;
            string destination = String.IsNullOrEmpty(relativeFolder)
                ? outputRoot
                : Path.GetFullPath(Path.Combine(outputRoot, relativeFolder));
            if (!String.IsNullOrEmpty(relativeFolder))
                EnsureWithin(outputRoot, destination);

            try
            {
                byte[] payload = new byte[bytes.Length - payloadOffset];
                Buffer.BlockCopy(bytes, payloadOffset, payload, 0, payload.Length);
                NSBTX archive = new NSBTX(payload);
                if (archive.TexPlttSet == null || archive.TexPlttSet.dictTex == null)
                    throw new InvalidDataException("The MKDS parser returned no texture dictionary.");

                parsed++;
                HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int textureIndex = 0; textureIndex < archive.TexPlttSet.dictTex.numEntry; textureIndex++)
                {
                    KeyValuePair<string, NSBTX.TexplttSet.DictTexData> textureEntry = archive.TexPlttSet.dictTex[textureIndex];
                    NSBTX.TexplttSet.DictTexData texture = textureEntry.Value;
                    NSBTX.TexplttSet.DictPlttData palette = GetPalette(archive.TexPlttSet, textureEntry.Key, textureIndex, (int)texture.Fmt);
                    string fileName = ReserveName(textureEntry.Key, "texture_" + textureIndex.ToString("D2"), usedNames) + ".png";
                    string imagePath = Path.GetFullPath(Path.Combine(destination, fileName));
                    EnsureWithin(outputRoot, imagePath);

                    using (Bitmap bitmap = texture.ToBitmap(palette))
                    {
                        if (bitmap == null)
                            throw new InvalidDataException("The MKDS decoder returned an empty bitmap.");
                        Directory.CreateDirectory(destination);
                        bitmap.Save(imagePath, ImageFormat.Png);
                    }
                    exported++;
                }

                Console.WriteLine("Rendered: " + relativePath);
            }
            catch (Exception exception)
            {
                failed++;
                if (Directory.Exists(destination))
                    Directory.Delete(destination, true);
                Console.Error.WriteLine("Failed: " + relativePath + " (" + exception.Message + ")");
            }
        }

        Console.WriteLine("BIN files scanned: " + binaries.Count + "; NSBTX containers: " + parsed + "; PNGs: " + exported + "; failed: " + failed);
        Console.WriteLine("Output: " + outputRoot);
        return failed == 0 ? 0 : 1;
    }

    private static int FindTexturePayload(byte[] bytes)
    {
        int lastOffset = Math.Min(bytes.Length - 24, 64);
        for (int offset = 0; offset <= lastOffset; offset++)
        {
            bool hasSignature = bytes[offset] == 'B' && bytes[offset + 1] == 'T'
                && bytes[offset + 2] == 'X' && bytes[offset + 3] == '0';
            bool hasByteOrderMark = bytes[offset + 4] == 0xFF && bytes[offset + 5] == 0xFE
                || bytes[offset + 4] == 0xFE && bytes[offset + 5] == 0xFF;
            bool hasTextureBlock = bytes[offset + 20] == 'T' && bytes[offset + 21] == 'E'
                && bytes[offset + 22] == 'X' && bytes[offset + 23] == '0';
            if (hasSignature && hasByteOrderMark && hasTextureBlock)
                return offset;
        }
        return -1;
    }

    private static NSBTX.TexplttSet.DictPlttData GetPalette(
        NSBTX.TexplttSet textureSet,
        string textureName,
        int textureIndex,
        int format)
    {
        if (format == 7 || textureSet.dictPltt == null || textureSet.dictPltt.numEntry == 0)
            return null;

        string[] paletteNames = { textureName + "_pl", textureName + "_pal", textureName + "pl" };
        for (int paletteIndex = 0; paletteIndex < textureSet.dictPltt.numEntry; paletteIndex++)
        {
            KeyValuePair<string, NSBTX.TexplttSet.DictPlttData> paletteEntry = textureSet.dictPltt[paletteIndex];
            foreach (string paletteName in paletteNames)
            {
                if (String.Equals(paletteEntry.Key, paletteName, StringComparison.OrdinalIgnoreCase))
                    return paletteEntry.Value;
            }
        }

        int fallbackIndex = Math.Min(textureIndex, textureSet.dictPltt.numEntry - 1);
        return textureSet.dictPltt[fallbackIndex].Value;
    }

    private static void ExtractDirectory(NARC.DirectoryEntry entry, string destination, ref int extracted)
    {
        Directory.CreateDirectory(destination);
        HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (NARC.FileEntry file in entry.Files)
        {
            string fallback = "file_" + file.Id.ToString("D4");
            string fileName = ReserveName(file.Name, fallback, usedNames);
            string filePath = Path.GetFullPath(Path.Combine(destination, fileName));
            EnsureWithin(destination, filePath);
            File.WriteAllBytes(filePath, file.Content ?? new byte[0]);
            extracted++;
        }

        foreach (NARC.DirectoryEntry subdirectory in entry.Subdirs)
        {
            string fallback = "dir_" + subdirectory.Id.ToString("D4");
            string directoryName = ReserveName(subdirectory.Name, fallback, usedNames);
            string directoryPath = Path.GetFullPath(Path.Combine(destination, directoryName));
            EnsureWithin(destination, directoryPath);
            ExtractDirectory(subdirectory, directoryPath, ref extracted);
        }
    }

    private static string ReserveName(string name, string fallback, HashSet<string> usedNames)
    {
        string safeName = SanitizeName(name, fallback);
        string candidate = safeName;
        int suffix = 1;
        while (!usedNames.Add(candidate))
        {
            candidate = safeName + "_" + suffix.ToString("D2");
            suffix++;
        }
        return candidate;
    }

    private static string SanitizeName(string name, string fallback)
    {
        if (String.IsNullOrWhiteSpace(name))
            return fallback;

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        char[] characters = name.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            if (Array.IndexOf(invalidCharacters, characters[index]) >= 0 || characters[index] == '/' || characters[index] == '\\')
                characters[index] = '_';
        }

        string safeName = new string(characters).Trim().TrimEnd('.');
        return safeName == "." || safeName == ".." || safeName.Length == 0 ? fallback : safeName;
    }

    private static void EnsureWithin(string parent, string path)
    {
        string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Archive entry resolves outside its output folder.");
    }

    private static bool IsWithin(string parent, string path)
    {
        string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }
}