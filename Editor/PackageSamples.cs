using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Glitchr.Store
{
    /// <summary>A sample a package declares in its package.json ("samples": displayName, description, path).</summary>
    public sealed class PackageSample
    {
        public string DisplayName;
        public string Description;
        /// <summary>Its folder, relative to the package's root (usually Samples~/Name).</summary>
        public string Path;
    }

    /// <summary>
    /// An installed package's samples, imported as Unity's own window does: copied into
    /// Assets/Samples/&lt;package display name&gt;/&lt;version&gt;/&lt;sample display name&gt;. In the
    /// editor, Unity's Sample API does the import when it knows the sample; the copy here is the
    /// same, for the rest. And whether a package can be embedded (copied into Packages/).
    /// </summary>
    public static class PackageSamples
    {
        public static List<PackageSample> Read(string packageJson)
        {
            var samples = new List<PackageSample>();
            foreach (object entry in Json.Array(Json.Get(Json.Parse(packageJson), "samples")) ?? new List<object>())
            {
                string path = Json.String(Json.Get(entry, "path"));
                if (string.IsNullOrEmpty(path)) continue;
                samples.Add(new PackageSample
                {
                    DisplayName = Json.String(Json.Get(entry, "displayName")) ?? path.TrimEnd('/').Split('/').Last(),
                    Description = Json.String(Json.Get(entry, "description")),
                    Path = path,
                });
            }

            return samples;
        }

        /// <summary>Where the sample goes, as Unity names it: Assets/Samples/&lt;package&gt;/&lt;version&gt;/&lt;sample&gt;.</summary>
        public static string ImportFolder(string assetsFolder, string packageDisplayName, string version, string sampleDisplayName)
        {
            return System.IO.Path.Combine(assetsFolder, "Samples", SafeName(packageDisplayName), version, SafeName(sampleDisplayName));
        }

        /// <summary>A display name as a folder name, as Unity's Sample names it: each character a file system refuses (on any of them) becomes "_".</summary>
        public static string SafeName(string name)
        {
            var invalid = new HashSet<char>(System.IO.Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"));

            return new string((name ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        }

        /// <summary>The sample's folder in the package, refused when its path leaves the package.</summary>
        public static string SourceFolder(string packageFolder, PackageSample sample)
        {
            string root = System.IO.Path.GetFullPath(packageFolder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(packageFolder, sample.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!(folder + System.IO.Path.DirectorySeparatorChar).StartsWith(root, StringComparison.Ordinal)) throw new ArgumentException(sample.DisplayName + ": its path leaves the package.");
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(sample.DisplayName + ": the package has no folder " + sample.Path + ".");

            return folder;
        }

        /// <summary>The sample's files copied to <paramref name="destination"/> (replaced when <paramref name="replace"/>); returns the number of files.</summary>
        public static int Copy(string packageFolder, PackageSample sample, string destination, bool replace = false)
        {
            string source = SourceFolder(packageFolder, sample);
            if (Directory.Exists(destination))
            {
                if (!replace) throw new IOException(sample.DisplayName + " is already imported in " + destination + ".");
                Directory.Delete(destination, true);
                if (File.Exists(destination + ".meta")) File.Delete(destination + ".meta");
            }
            int count = 0;
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length).TrimStart(System.IO.Path.DirectorySeparatorChar);
                string target = System.IO.Path.Combine(destination, relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                File.Copy(file, target);
                count++;
            }

            return count;
        }

        /// <summary>
        /// Whether Embed applies: a package Unity keeps in its cache (a registry's or a Git one),
        /// not a built-in module, a folder of the user's or one already embedded.
        /// </summary>
        public static bool Embeddable(InstalledPackage package, string packagesFolder)
        {
            if (package == null || package.Source != InstalledPackage.Registry && package.Source != InstalledPackage.Git) return false;
            if (package.Name.StartsWith("com.unity.modules.", StringComparison.Ordinal)) return false;

            return packagesFolder == null || !Directory.Exists(System.IO.Path.Combine(packagesFolder, package.Name));
        }
    }
}
