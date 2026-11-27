using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nekoare.ClickRecolor.Editor.UpdateCheck
{
    /// <summary>x.y.z の版。プレリリース（-beta 等）やビルド情報付きは扱わない</summary>
    internal readonly struct PackageVersion : IComparable<PackageVersion>
    {
        public readonly int Major;
        public readonly int Minor;
        public readonly int Patch;

        public PackageVersion(int major, int minor, int patch)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        // 正規表現の $ は末尾の改行の手前でも一致し、char.IsDigit は全角数字なども通すため、1 文字ずつ ASCII 数字か確かめる。
        // ネットから来た文字列なので、通したものは必ず「数字.数字.数字」に作り直してから表示する
        public static bool TryParse(string s, out PackageVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split('.');
            if (parts.Length != 3) return false;
            var numbers = new int[3];
            for (int i = 0; i < 3; i++)
            {
                var part = parts[i];
                if (part.Length == 0) return false;
                foreach (var c in part)
                    if (c < '0' || c > '9') return false;
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
            }
            version = new PackageVersion(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        public int CompareTo(PackageVersion other)
        {
            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            return Patch.CompareTo(other.Patch);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    internal enum UpdateNoticeKind { None, Consent, Update }

    internal static class UpdateCheckLogic
    {
        /// <summary>
        /// VPM listing から、プレリリースと今の Unity で入れられない版を除いた一番新しい版を返す。読めなければ null
        /// </summary>
        /// <param name="unityVersion">Application.unityVersion の形（例: 2022.3.22f1）</param>
        public static PackageVersion? PickLatest(string listingJson, string packageName, string unityVersion)
        {
            if (string.IsNullOrEmpty(listingJson)) return null;
            object root;
            try
            {
                root = MiniJsonReader.Read(listingJson);
            }
            catch (FormatException)
            {
                return null;
            }

            if (!(root is Dictionary<string, object> listing)) return null;
            if (!(listing.TryGetValue("packages", out var packagesValue) && packagesValue is Dictionary<string, object> packages)) return null;
            if (!(packages.TryGetValue(packageName, out var packageValue) && packageValue is Dictionary<string, object> package)) return null;
            if (!(package.TryGetValue("versions", out var versionsValue) && versionsValue is Dictionary<string, object> versions)) return null;

            bool hasCurrentUnity = TryParseUnityMajorMinor(unityVersion, out var currentUnity);
            PackageVersion? latest = null;
            foreach (var entryValue in versions.Values)
            {
                if (!(entryValue is Dictionary<string, object> entry)) continue;
                if (!(entry.TryGetValue("version", out var versionValue) && versionValue is string versionText)) continue;
                if (!PackageVersion.TryParse(versionText, out var version)) continue;

                if (hasCurrentUnity
                    && entry.TryGetValue("unity", out var unityValue) && unityValue is string requiredUnityText
                    && TryParseUnityMajorMinor(requiredUnityText, out var requiredUnity)
                    && CompareMajorMinor(requiredUnity, currentUnity) > 0)
                    continue;

                if (latest == null || version.CompareTo(latest.Value) > 0) latest = version;
            }
            return latest;
        }

        public static bool ShouldNotify(PackageVersion current, PackageVersion? latest, PackageVersion? skipped)
        {
            if (latest == null) return false;
            if (latest.Value.CompareTo(current) <= 0) return false;
            return skipped == null || latest.Value.CompareTo(skipped.Value) > 0;
        }

        /// <summary>Scene のお知らせに何を出すか。［閉じる］は新しい版のお知らせにだけ置くので、dismissed は同意より先に見てよい</summary>
        public static UpdateNoticeKind Decide(PackageVersion? current, bool hasChosen, bool enabled, bool requesting,
            PackageVersion? latest, PackageVersion? skipped, bool dismissed)
        {
            // VPM 以外で入れていて版が取れないときは、比べようがないので聞きもしない
            if (current == null || dismissed) return UpdateNoticeKind.None;
            if (!hasChosen) return UpdateNoticeKind.Consent;
            if (!enabled || requesting) return UpdateNoticeKind.None;
            return ShouldNotify(current.Value, latest, skipped) ? UpdateNoticeKind.Update : UpdateNoticeKind.None;
        }

        public static bool IsMajorUpdate(PackageVersion current, PackageVersion latest) => latest.Major > current.Major;

        /// <summary>「2022.3」「2022.3.22f1」の先頭 2 つの数を読む</summary>
        private static bool TryParseUnityMajorMinor(string s, out (int major, int minor) result)
        {
            result = default;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return false;
            result = (major, minor);
            return true;
        }

        private static int CompareMajorMinor((int major, int minor) a, (int major, int minor) b)
            => a.major != b.major ? a.major.CompareTo(b.major) : a.minor.CompareTo(b.minor);
    }

    /// <summary>
    /// listing を読むための小さい JSON 読み取り。object は Dictionary、array は List、数は double になる。
    /// JsonUtility は版をキーにした辞書（versions）を読めず、Newtonsoft に依存すると VCC の依存解決で失敗する余地が増えるため自前で持つ。
    /// ネットから来た文字列を読むので、入れ子の深さに上限を設けて再帰のスタック溢れを防ぐ
    /// </summary>
    internal sealed class MiniJsonReader
    {
        private const int MaxDepth = 64;

        private readonly string _text;
        private int _pos;
        private int _depth;

        private MiniJsonReader(string text) => _text = text;

        /// <exception cref="FormatException">JSON として読めない・入れ子が深すぎる</exception>
        public static object Read(string text)
        {
            var reader = new MiniJsonReader(text);
            var value = reader.ReadValue();
            reader.SkipWhitespace();
            if (reader._pos != text.Length) throw new FormatException("trailing characters");
            return value;
        }

        private object ReadValue()
        {
            SkipWhitespace();
            if (_pos >= _text.Length) throw new FormatException("unexpected end");
            switch (_text[_pos])
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return ReadString();
                case 't': ExpectWord("true"); return true;
                case 'f': ExpectWord("false"); return false;
                case 'n': ExpectWord("null"); return null;
                default: return ReadNumber();
            }
        }

        private Dictionary<string, object> ReadObject()
        {
            Enter();
            _pos++; // {
            var result = new Dictionary<string, object>();
            SkipWhitespace();
            if (Peek() == '}') { _pos++; _depth--; return result; }
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"') throw new FormatException("expected key");
                var key = ReadString();
                SkipWhitespace();
                if (Peek() != ':') throw new FormatException("expected ':'");
                _pos++;
                result[key] = ReadValue();
                SkipWhitespace();
                var c = Peek();
                _pos++;
                if (c == ',') continue;
                if (c == '}') break;
                throw new FormatException("expected ',' or '}'");
            }
            _depth--;
            return result;
        }

        private List<object> ReadArray()
        {
            Enter();
            _pos++; // [
            var result = new List<object>();
            SkipWhitespace();
            if (Peek() == ']') { _pos++; _depth--; return result; }
            while (true)
            {
                result.Add(ReadValue());
                SkipWhitespace();
                var c = Peek();
                _pos++;
                if (c == ',') continue;
                if (c == ']') break;
                throw new FormatException("expected ',' or ']'");
            }
            _depth--;
            return result;
        }

        private string ReadString()
        {
            _pos++; // "
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length) throw new FormatException("unterminated string");
                var c = _text[_pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (_pos >= _text.Length) throw new FormatException("unterminated escape");
                var e = _text[_pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_pos + 4 > _text.Length
                            || !int.TryParse(_text.Substring(_pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            throw new FormatException("bad \\u escape");
                        sb.Append((char)code);
                        _pos += 4;
                        break;
                    default: throw new FormatException("bad escape");
                }
            }
        }

        private double ReadNumber()
        {
            int start = _pos;
            while (_pos < _text.Length && "+-0123456789.eE".IndexOf(_text[_pos]) >= 0) _pos++;
            if (_pos == start
                || !double.TryParse(_text.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new FormatException("bad number");
            return number;
        }

        private void ExpectWord(string word)
        {
            if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) throw new FormatException("bad literal");
            _pos += word.Length;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw new FormatException("too deep");
        }

        private char Peek()
        {
            if (_pos >= _text.Length) throw new FormatException("unexpected end");
            return _text[_pos];
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && (_text[_pos] == ' ' || _text[_pos] == '\t' || _text[_pos] == '\n' || _text[_pos] == '\r')) _pos++;
        }
    }
}
