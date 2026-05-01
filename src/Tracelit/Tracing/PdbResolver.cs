using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Tracelit.Tracing;

/// <summary>
/// Resolves the source file path and line number for a <see cref="MethodInfo"/>
/// by reading Portable PDB data. Tries in order:
///   1. Embedded PDB inside the PE image (<c>&lt;DebugType&gt;embedded&lt;/DebugType&gt;</c>).
///   2. External <c>.pdb</c> file next to the assembly — present in every Debug build automatically.
/// When neither is available the method returns <c>(null, 0)</c> silently;
/// instrumentation continues to work without file/line attributes.
///
/// Results are cached per method token — overhead is paid only once per action.
/// </summary>
internal static class PdbResolver
{
    private static readonly ConcurrentDictionary<int, (string? File, int Line)> _cache = new();

    internal static (string? File, int Line) TryResolve(MethodInfo method)
        => _cache.GetOrAdd(method.MetadataToken, _ => Resolve(method));

    private static (string? File, int Line) Resolve(MethodInfo method)
    {
        try
        {
            var assembly = method.DeclaringType?.Assembly;
            if (assembly is null) return default;

            var location = assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location)) return default;

            // ── Pass 1: embedded PDB (Release with <DebugType>embedded</DebugType>) ──
            using (var peStream = File.OpenRead(location))
            using (var peReader = new PEReader(peStream))
            {
                if (peReader.HasMetadata)
                {
                    var embeddedEntry = peReader.ReadDebugDirectory()
                        .FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

                    if (embeddedEntry.DataSize > 0)
                    {
                        using var pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedEntry);
                        var hit = ReadFromPdb(pdbProvider.GetMetadataReader(), method.MetadataToken);
                        if (hit.File is not null) return hit;
                    }
                }
            }

            // ── Pass 2: external .pdb next to the assembly (every Debug build, zero config) ──
            var pdbPath = Path.ChangeExtension(location, ".pdb");
            if (File.Exists(pdbPath))
            {
                try
                {
                    using var pdbStream = File.OpenRead(pdbPath);
                    using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                    var hit = ReadFromPdb(pdbProvider.GetMetadataReader(), method.MetadataToken);
                    if (hit.File is not null) return hit;
                }
                catch
                {
                    // Non-portable (legacy Windows) PDB format — skip gracefully.
                }
            }
        }
        catch
        {
            // Any other failure (trimmed, AOT, restricted I/O) — degrade gracefully.
        }

        return default;
    }

    private static (string? File, int Line) ReadFromPdb(MetadataReader pdbReader, int metadataToken)
    {
        var methodHandle = MetadataTokens.MethodDefinitionHandle(metadataToken);
        var debugInfo = pdbReader.GetMethodDebugInformation(methodHandle);

        foreach (var sp in debugInfo.GetSequencePoints())
        {
            if (sp.IsHidden) continue;
            var rawPath = pdbReader.GetString(pdbReader.GetDocument(sp.Document).Name);
            return (rawPath.Replace('\\', '/'), sp.StartLine);
        }

        return default;
    }
}
