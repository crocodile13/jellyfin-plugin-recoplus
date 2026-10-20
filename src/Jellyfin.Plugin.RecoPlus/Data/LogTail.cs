using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>Une entrée du journal de Jellyfin.</summary>
public sealed record LogEntry(string Time, string Level, string Category, string Message);

/// <summary>Morceau de journal lu depuis un curseur, et le curseur suivant.</summary>
public sealed record LogChunk(string Cursor, List<LogEntry> Entries, string File);

/// <summary>
/// Suivi du journal de Jellyfin (onglet Journal, mode développeur) : lit la fin du fichier de log du jour à partir d'un
/// curseur (« fichier|position »), garde les entrées de Reco+ (ou toutes), masque ce qui ressemble à un secret. Rien
/// n'est écrit ; le nom de fichier du curseur n'est jamais utilisé comme chemin.
/// </summary>
public static partial class LogTail
{
    /// <summary>Au premier appel, on remonte de cette taille dans le fichier.</summary>
    public const int StartBack = 256 * 1024;

    /// <summary>Lu au plus par appel.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Entrées renvoyées au plus par appel (les plus récentes).</summary>
    public const int MaxEntries = 500;

    private const int MaxMessage = 8000;

    [GeneratedRegex(@"^\[(?<t>[^\]]{1,40})\] \[(?<l>[A-Z]{3})\] \[\d+\] (?<c>[^:\s]{1,200}): ?(?<m>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    [GeneratedRegex(@"(?i)(api[_-]?key|x-api-key|x-emby-token|x-mediabrowser-token|access[_-]?token|apitoken|token|password|authorization|bearer)(\s*[""']?\s*[:=]\s*[""']?|\s+)((?:bearer|basic|mediabrowser)\s+)?([^\s""'&,;}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Secret();

    /// <summary>Masque les valeurs qui suivent un nom de secret (clé d'API, jeton, mot de passe…).</summary>
    public static string Redact(string s) => Secret().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + "***");

    /// <summary>Entrée de Reco+ (catégorie du plugin, ou message qui le nomme).</summary>
    public static bool IsReco(LogEntry e)
        => e.Category.StartsWith("Jellyfin.Plugin.RecoPlus", StringComparison.Ordinal) || e.Message.Contains("Reco+", StringComparison.Ordinal);

    /// <summary>
    /// Découpe des lignes en entrées : une ligne « [heure] [NIV] [fil] Catégorie: message » ouvre une entrée, les lignes
    /// suivantes (pile d'exception) s'y ajoutent. Lignes avant la première entrée ignorées.
    /// </summary>
    public static List<LogEntry> Parse(IEnumerable<string> lines, bool all)
    {
        var res = new List<LogEntry>();
        StringBuilder? msg = null;
        (string T, string L, string C)? head = null;
        bool keep = false;

        void Close()
        {
            if (head is { } h && msg is not null)
            {
                var e = new LogEntry(h.T, h.L, h.C, Redact(msg.ToString()));
                if (keep || all || IsReco(e))
                {
                    res.Add(e);
                }
            }

            (head, msg, keep) = (null, null, false);
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var m = Line().Match(line);
            if (m.Success)
            {
                Close();
                head = (m.Groups["t"].Value, m.Groups["l"].Value, m.Groups["c"].Value);
                msg = new StringBuilder(m.Groups["m"].Value);
                keep = head.Value.C.StartsWith("Jellyfin.Plugin.RecoPlus", StringComparison.Ordinal);
            }
            else if (msg is not null && line.Length > 0 && msg.Length < MaxMessage)
            {
                msg.Append('\n').Append(line.Length > MaxMessage ? line[..MaxMessage] : line);
                keep |= line.Contains("RecoPlus", StringComparison.Ordinal);
            }
        }

        Close();
        return res;
    }

    /// <summary>
    /// Lit les entrées écrites depuis le curseur dans le fichier de log le plus récent du dossier. Sans curseur : la fin
    /// du fichier. Fichier changé (nouveau jour) : depuis son début.
    /// </summary>
    public static LogChunk Read(string dir, string? cursor, bool all)
    {
        var file = Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (file is null)
        {
            return new LogChunk(string.Empty, new List<LogEntry>(), string.Empty);
        }

        string? curFile = null;
        long curPos = -1;
        if (!string.IsNullOrEmpty(cursor) && cursor.LastIndexOf('|') is int bar and > 0
            && long.TryParse(cursor.AsSpan(bar + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p))
        {
            (curFile, curPos) = (cursor[..bar], p);
        }

        long len = file.Length;
        long start;
        bool partial;
        if (curFile == file.Name && curPos >= 0 && curPos <= len)
        {
            (start, partial) = (curPos, false);
        }
        else if (curFile is not null && curFile != file.Name)
        {
            (start, partial) = (0, false);                         // nouveau fichier depuis le dernier appel
        }
        else
        {
            start = Math.Max(0, len - StartBack);                   // premier appel (ou fichier raccourci) : la fin
            partial = start > 0;
        }

        int want = (int)Math.Min(MaxBytes, len - start);
        if (want <= 0)
        {
            return new LogChunk(file.Name + "|" + start, new List<LogEntry>(), file.Name);
        }

        var buf = new byte[want];
        int got = 0;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Seek(start, SeekOrigin.Begin);
            while (got < want)
            {
                int n = fs.Read(buf, got, want - got);
                if (n <= 0)
                {
                    break;
                }

                got += n;
            }
        }

        int last = Array.LastIndexOf(buf, (byte)'\n', Math.Max(0, got - 1));
        if (got == 0 || last < 0)
        {
            return new LogChunk(file.Name + "|" + start, new List<LogEntry>(), file.Name);   // pas encore de ligne complète
        }

        var lines = Encoding.UTF8.GetString(buf, 0, last + 1).Split('\n');
        var entries = Parse(partial ? lines.Skip(1) : lines, all);
        if (entries.Count > MaxEntries)
        {
            entries = entries.GetRange(entries.Count - MaxEntries, MaxEntries);
        }

        return new LogChunk(file.Name + "|" + (start + last + 1), entries, file.Name);
    }
}
