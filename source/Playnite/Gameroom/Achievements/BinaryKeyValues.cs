using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Playnite.Gameroom.Achievements
{
    /// <summary>
    /// Node of a Steam binary KeyValues file, the format of the achievement files in Steam\appcache\stats.
    /// </summary>
    public class KeyValue
    {
        public string Name { get; }
        public object Value { get; }
        public List<KeyValue> Children { get; } = new List<KeyValue>();

        public KeyValue(string name, object value)
        {
            Name = name;
            Value = value;
        }

        public KeyValue this[string name] => Children.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        public string AsString()
        {
            return Value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : Value as string;
        }

        public long? AsLong()
        {
            switch (Value)
            {
                case int i:
                    return i;
                case long l:
                    return l;
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                default:
                    return null;
            }
        }

        public override string ToString()
        {
            return Children.Count > 0 ? $"{Name} ({Children.Count})" : $"{Name} = {Value}";
        }
    }

    public static class BinaryKeyValues
    {
        private const byte typeSection = 0;
        private const byte typeString = 1;
        private const byte typeInt32 = 2;
        private const byte typeFloat = 3;
        private const byte typePointer = 4;
        private const byte typeWideString = 5;
        private const byte typeColor = 6;
        private const byte typeUInt64 = 7;
        private const byte typeEnd = 8;
        private const byte typeInt64 = 10;
        private const byte typeAlternateEnd = 11;

        public static KeyValue Read(string path)
        {
            return Read(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Returns a root node whose children are the top level entries of the file.
        /// </summary>
        public static KeyValue Read(byte[] data)
        {
            var root = new KeyValue(string.Empty, null);
            var position = 0;
            ReadChildren(data, ref position, root);
            return root;
        }

        private static void ReadChildren(byte[] data, ref int position, KeyValue parent)
        {
            while (position < data.Length)
            {
                var type = data[position++];
                if (type == typeEnd || type == typeAlternateEnd)
                {
                    return;
                }

                var name = ReadString(data, ref position);
                switch (type)
                {
                    case typeSection:
                        var section = new KeyValue(name, null);
                        ReadChildren(data, ref position, section);
                        parent.Children.Add(section);
                        break;
                    case typeString:
                        parent.Children.Add(new KeyValue(name, ReadString(data, ref position)));
                        break;
                    case typeWideString:
                        parent.Children.Add(new KeyValue(name, ReadWideString(data, ref position)));
                        break;
                    case typeInt32:
                    case typePointer:
                    case typeColor:
                        Require(data, position, 4);
                        parent.Children.Add(new KeyValue(name, BitConverter.ToInt32(data, position)));
                        position += 4;
                        break;
                    case typeFloat:
                        Require(data, position, 4);
                        parent.Children.Add(new KeyValue(name, BitConverter.ToSingle(data, position)));
                        position += 4;
                        break;
                    case typeUInt64:
                    case typeInt64:
                        Require(data, position, 8);
                        parent.Children.Add(new KeyValue(name, BitConverter.ToInt64(data, position)));
                        position += 8;
                        break;
                    default:
                        throw new InvalidDataException($"Unknown KeyValues type {type} at {position - 1}.");
                }
            }
        }

        private static void Require(byte[] data, int position, int length)
        {
            if (position + length > data.Length)
            {
                throw new InvalidDataException("Unexpected end of KeyValues data.");
            }
        }

        private static string ReadString(byte[] data, ref int position)
        {
            var end = Array.IndexOf(data, (byte)0, position);
            if (end < 0)
            {
                throw new InvalidDataException("Unterminated KeyValues string.");
            }

            var value = Encoding.UTF8.GetString(data, position, end - position);
            position = end + 1;
            return value;
        }

        private static string ReadWideString(byte[] data, ref int position)
        {
            var end = position;
            while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
            {
                end += 2;
            }

            var value = Encoding.Unicode.GetString(data, position, end - position);
            position = end + 2;
            return value;
        }
    }
}
