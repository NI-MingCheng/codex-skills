using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EncodingChecker.Cli
{
    internal static class Detection
    {
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(
            ".exe,.dll,.pdb,.obj,.o,.a,.lib,.bin,.png,.jpg,.jpeg,.gif,.bmp,.ico,.webp,.pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.zip,.7z,.rar,.gz,.bz2,.xz,.tar,.mp3,.mp4,.wav,.avi,.mov,.woff,.woff2,.ttf,.otf,.db,.sqlite,.class,.pyc,.npy,.npz,.dcm".Split(','),
            StringComparer.OrdinalIgnoreCase);

        internal static string Canonical(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("An encoding is required.");
            switch (name.Trim().ToLowerInvariant().Replace('_', '-'))
            {
                case "utf8": case "utf-8": case "65001": case "cp65001": return "utf-8";
                case "ascii": case "us-ascii": case "20127": return "ascii";
                case "gbk": case "gb2312": case "cp936": case "windows-936": case "936": return "gbk";
                case "gb18030": case "gb-18030": case "54936": case "cp54936": return "gb18030";
                case "windows-1252": case "windows1252": case "cp1252": case "1252": return "windows-1252";
                case "big5": case "big-5": case "csbig5": case "cp950": case "windows-950": case "950": return "big5";
                case "shift-jis": case "shiftjis": case "sjis": case "ms-kanji": case "cp932": case "windows-932": case "windows-31j": case "windows31j": case "932": return "shift-jis";
                case "utf-16": case "utf16": case "utf-16le": case "utf-16-le": case "unicode": case "1200": return "utf-16-le";
                case "utf-16be": case "utf-16-be": case "unicodefffe": case "1201": return "utf-16-be";
                case "utf-32": case "utf32": case "utf-32le": case "utf-32-le": case "12000": return "utf-32-le";
                case "utf-32be": case "utf-32-be": case "12001": return "utf-32-be";
                default: throw new ArgumentException("Unsupported encoding: " + name + ". Supported: utf-8, ascii, gbk, gb18030, windows-1252, big5, shift-jis, utf-16-le/be, utf-32-le/be.");
            }
        }

        internal static Encoding Codec(string name)
        {
            switch (Canonical(name))
            {
                case "utf-8": return Utf8;
                case "ascii": return Encoding.GetEncoding(20127, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                case "gbk": return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                case "gb18030": return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                case "windows-1252": return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                case "big5": return Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                case "shift-jis": return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                case "utf-16-le": return new UnicodeEncoding(false, false, true);
                case "utf-16-be": return new UnicodeEncoding(true, false, true);
                case "utf-32-le": return new UTF32Encoding(false, false, true);
                case "utf-32-be": return new UTF32Encoding(true, false, true);
                default: throw new ArgumentException("Unsupported encoding.");
            }
        }

        internal static string Bom(byte[] b, out int size)
        {
            size = 0;
            if (Starts(b, 0xFF, 0xFE, 0, 0)) { size = 4; return "utf-32-le"; }
            if (Starts(b, 0, 0, 0xFE, 0xFF)) { size = 4; return "utf-32-be"; }
            if (Starts(b, 0xEF, 0xBB, 0xBF)) { size = 3; return "utf-8"; }
            if (Starts(b, 0xFF, 0xFE)) { size = 2; return "utf-16-le"; }
            if (Starts(b, 0xFE, 0xFF)) { size = 2; return "utf-16-be"; }
            // Recognized, unsupported BOMs must not fall through to a guess.
            if (Starts(b, 0x2B, 0x2F, 0x76) && b.Length > 3 && new byte[] { 0x38, 0x39, 0x2B, 0x2F }.Contains(b[3]))
                throw new ArgumentException("Unsupported UTF-7 BOM.");
            if (Starts(b, 0x84, 0x31, 0x95, 0x33)) throw new ArgumentException("Unsupported GB18030 BOM convention.");
            return null;
        }

        internal static string Decode(byte[] raw, string encoding)
        {
            int offset;
            string bom = Bom(raw, out offset);
            string canonical = Canonical(encoding);
            if (bom != null && bom != canonical) throw new InvalidDataException("BOM conflicts with encoding " + canonical + ".");
            Encoding codec = Codec(canonical);
            string text = codec.GetString(raw, offset, raw.Length - offset);
            byte[] roundtrip = codec.GetBytes(text);
            if (roundtrip.Length != raw.Length - offset || !roundtrip.SequenceEqual(raw.Skip(offset)))
                throw new InvalidDataException("The complete byte sequence does not round-trip under " + canonical + ".");
            return text;
        }

        internal static bool TryDecode(byte[] raw, string encoding, out string text)
        {
            try { text = Decode(raw, encoding); return true; }
            catch (DecoderFallbackException) { text = null; return false; }
            catch (EncoderFallbackException) { text = null; return false; }
            catch (InvalidDataException) { text = null; return false; }
        }

        private static bool Starts(byte[] b, params byte[] prefix)
        {
            return b.Length >= prefix.Length && b.Take(prefix.Length).SequenceEqual(prefix);
        }

        private static bool BinarySignature(byte[] b)
        {
            return Starts(b, 0x4D, 0x5A) || Starts(b, 0x7F, 0x45, 0x4C, 0x46) ||
                Starts(b, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) ||
                Starts(b, 0x50, 0x4B, 0x03, 0x04) || Starts(b, 0x50, 0x4B, 0x05, 0x06) ||
                Starts(b, 0x1F, 0x8B) || Starts(b, 0x25, 0x50, 0x44, 0x46) ||
                Starts(b, 0x47, 0x49, 0x46, 0x38) || Starts(b, 0xFF, 0xD8, 0xFF) ||
                Starts(b, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C) || Starts(b, 0x52, 0x61, 0x72, 0x21);
        }

        private static bool BadControl(char c)
        {
            return Char.IsControl(c) && c != '\t' && c != '\r' && c != '\n' && c != '\f';
        }

        internal static EolInfo Eol(string text)
        {
            var result = new EolInfo();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') { result.crlf++; i++; }
                    else result.cr++;
                }
                else if (text[i] == '\n') result.lf++;
            }
            int kinds = (result.crlf > 0 ? 1 : 0) + (result.lf > 0 ? 1 : 0) + (result.cr > 0 ? 1 : 0);
            result.kind = kinds > 1 ? "mixed" : result.crlf > 0 ? "crlf" : result.lf > 0 ? "lf" : result.cr > 0 ? "cr" : "none";
            result.final_newline = text.EndsWith("\r", StringComparison.Ordinal) || text.EndsWith("\n", StringComparison.Ordinal);
            return result;
        }

        internal static FileRecord Probe(string path, string relative, string explicitEncoding, string fallback, bool previews)
        {
            SafeFiles.NoReparse(path);
            byte[] raw = File.ReadAllBytes(path);
            var record = new FileRecord { path = relative, sha256 = SafeFiles.Hash(raw), bytes = raw.LongLength };
            try { return Analyze(path, record, raw, explicitEncoding, fallback, previews); }
            catch (Exception error)
            {
                // Reading succeeded: preserve trustworthy byte metadata so confirm can repair a codec choice.
                record.encoding = null;
                record.status = "error";
                record.evidence.Add(error.GetType().Name + ": " + error.Message);
                Console.Error.WriteLine("probe: " + relative + ": " + error.Message);
                return record;
            }
        }

        private static FileRecord Analyze(string path, FileRecord record, byte[] raw, string explicitEncoding, string fallback, bool previews)
        {
            int bomSize;
            record.bom = Bom(raw, out bomSize);
            string chosen = explicitEncoding == null ? null : Canonical(explicitEncoding);
            if (chosen != null && record.bom != null && chosen != record.bom)
                throw new InvalidDataException("Explicit policy conflicts with BOM: " + record.path);
            if (BinaryExtensions.Contains(Path.GetExtension(path)) || BinarySignature(raw))
            {
                record.status = "binary";
                record.evidence.Add("binary-extension-or-signature");
                return record;
            }
            if (record.bom != null || chosen != null || fallback != null)
            {
                chosen = record.bom ?? chosen ?? Canonical(fallback);
                string text = Decode(raw, chosen);
                if (text.Any(BadControl))
                {
                    record.status = "binary";
                    record.evidence.Add("decoded-control-characters");
                    return record;
                }
                record.encoding = chosen;
                record.status = "confirmed";
                record.eol = Eol(text);
                record.evidence.Add(record.bom != null ? "bom" : explicitEncoding != null ? "explicit-encoding-policy" : "explicit-default-encoding-fallback");
                return record;
            }

            var texts = new Dictionary<string, string>();
            // NUL is a structural clue, never automatic proof of UTF-16/32.
            if (raw.Contains((byte)0))
            {
                Encoding structural = global::EncodingChecker.Utf16Detector.DetectFromBytes(raw);
                if (structural != null)
                {
                    string code = structural.CodePage == 1200 ? "utf-16-le" : structural.CodePage == 1201 ? "utf-16-be" :
                        structural.CodePage == 12000 ? "utf-32-le" : structural.CodePage == 12001 ? "utf-32-be" : null;
                    string text;
                    if (code != null && TryDecode(raw, code, out text) && !text.Any(BadControl))
                    {
                        texts[code] = text;
                        record.suggested_encoding = code;
                        record.evidence.Add("utf16-detector-structural-candidate-not-proof");
                    }
                }
                if (texts.Count == 0)
                {
                    record.status = "binary";
                    record.evidence.Add("nul-bytes-without-text-structure");
                    return record;
                }
            }
            else
            {
                if (raw.Any(b => b < 32 && b != 9 && b != 10 && b != 12 && b != 13) || raw.Contains((byte)127))
                {
                    record.status = "binary";
                    record.evidence.Add("control-bytes");
                    return record;
                }
                if (raw.All(b => b < 128))
                {
                    record.status = "ascii-compatible";
                    record.encoding = "ascii";
                    record.eol = Eol(Encoding.ASCII.GetString(raw));
                    record.evidence.Add("ascii-bytes-compatible-with-multiple-encodings-not-unique");
                    return record;
                }
                foreach (string code in new[] { "utf-8", "gbk", "gb18030" })
                {
                    string text;
                    if (TryDecode(raw, code, out text) && !text.Any(BadControl)) texts[code] = text;
                }
            }
            foreach (var candidate in texts)
                record.candidates.Add(new Candidate { encoding = candidate.Key, text_sha256 = SafeFiles.Hash(Utf8.GetBytes(candidate.Value)),
                    preview = previews ? Preview(candidate.Value) : null });
            int distinctTexts = texts.Values.Distinct(StringComparer.Ordinal).Count();
            record.status = texts.Count == 0 ? "unknown" : distinctTexts > 1 ? "ambiguous" : "needs-confirmation";
            if (texts.Count == 0) record.evidence.Add("no-automatic-strict-roundtrip-text-candidate");
            else
            {
                record.evidence.Add("complete-strict-decoder-encoder-byte-roundtrip");
                record.evidence.Add(distinctTexts > 1 ? "valid-encodings-produce-different-text" : "compatible-candidates-do-not-prove-original-encoding");
                // EOL is emitted only when all candidates agree on its exact counts.
                EolInfo first = Eol(texts.Values.First());
                if (texts.Values.Select(Eol).All(e => e.kind == first.kind && e.crlf == first.crlf && e.lf == first.lf && e.cr == first.cr && e.final_newline == first.final_newline))
                    record.eol = first;
                if (texts.Count == 1 && texts.ContainsKey("utf-8")) record.suggested_encoding = "utf-8";
                Suggest(raw, record, texts);
            }
            return record;
        }

        private static string Preview(string text)
        {
            int length = Math.Min(120, text.Length);
            if (length > 0 && length < text.Length && Char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length);
        }

        private static void Suggest(byte[] raw, FileRecord record, Dictionary<string, string> texts)
        {
            try
            {
                var detail = UtfUnknown.CharsetDetector.DetectFromBytes(raw).Detected;
                if (detail == null || detail.Encoding == null) return;
                string code;
                switch (detail.Encoding.CodePage)
                {
                    case 65001: code = "utf-8"; break;
                    case 936: code = "gbk"; break;
                    case 54936: code = "gb18030"; break;
                    case 1200: code = "utf-16-le"; break;
                    case 1201: code = "utf-16-be"; break;
                    default: return;
                }
                if (texts.ContainsKey(code))
                {
                    record.suggested_encoding = code;
                    record.confidence = Math.Round(detail.Confidence, 4);
                    record.evidence.Add("utfunknown-statistical-suggestion-not-confirmation");
                }
            }
            catch (Exception error)
            {
                record.evidence.Add("heuristic-unavailable: " + error.GetType().Name + ": " + error.Message);
            }
        }
    }
}
