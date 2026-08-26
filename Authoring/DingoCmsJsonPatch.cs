using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMSEditorServer.Authoring
{
    public static class DingoCmsJsonPatch
    {
        public static JToken ResolvePointer(JToken root, string pointer)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }
            if (pointer == null)
            {
                throw new ArgumentNullException(nameof(pointer));
            }
            if (pointer.Length == 0)
            {
                return root;
            }
            if (!pointer.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"JSON pointer '{pointer}' must start with '/'.");
            }

            JToken current = root;
            var parts = pointer.Substring(1).Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                var part = DecodePointerToken(parts[index]);
                if (current is JObject objectToken)
                {
                    current = objectToken[part]
                              ?? throw new InvalidDataException(
                                  $"JSON pointer '{pointer}' has no object member '{part}'.");
                    continue;
                }
                if (current is JArray arrayToken)
                {
                    var arrayIndex = RequirePointerArrayIndex(part, arrayToken.Count, pointer);
                    current = arrayToken[arrayIndex];
                    continue;
                }
                throw new InvalidDataException($"JSON pointer '{pointer}' traverses a scalar value.");
            }
            return current;
        }

        public static JObject Apply(JObject source, JArray patches)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (patches == null)
            {
                throw new ArgumentNullException(nameof(patches));
            }

            var result = (JObject)source.DeepClone();
            for (var index = 0; index < patches.Count; index++)
            {
                if (patches[index] is not JObject patch)
                {
                    throw new InvalidDataException($"JSON patch at index {index} must be an object.");
                }
                result = ApplyOne(result, patch, index);
            }
            return result;
        }

        private static JObject ApplyOne(JObject root, JObject patch, int patchIndex)
        {
            var operation = patch.Value<string>("op")
                            ?? throw new InvalidDataException($"JSON patch at index {patchIndex} has no op.");
            var path = patch.Value<string>("path")
                       ?? throw new InvalidDataException($"JSON patch at index {patchIndex} has no path.");
            if (path.Length == 0)
            {
                return ApplyRoot(root, patch, operation, patchIndex);
            }
            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"JSON patch path '{path}' must be an RFC 6901 pointer.");
            }

            var parts = path.Substring(1).Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                parts[index] = DecodePointerToken(parts[index]);
            }

            var parent = ResolveParent(root, parts, path);
            var leaf = parts[^1];
            switch (operation)
            {
                case "add":
                    Add(parent, leaf, RequireValue(patch, patchIndex), path);
                    break;
                case "set":
                    Set(parent, leaf, RequireValue(patch, patchIndex), path);
                    break;
                case "remove":
                    Remove(parent, leaf, path);
                    break;
                default:
                    throw new InvalidDataException(
                        $"JSON patch operation '{operation}' is unsupported. Use add, set, or remove.");
            }
            return root;
        }

        private static JObject ApplyRoot(JObject root, JObject patch, string operation, int patchIndex)
        {
            if (operation == "remove")
            {
                throw new InvalidDataException("The root GameAsset document cannot be removed by JSON patch.");
            }
            if (operation != "add" && operation != "set")
            {
                throw new InvalidDataException(
                    $"JSON patch operation '{operation}' is unsupported. Use add, set, or remove.");
            }
            if (RequireValue(patch, patchIndex) is not JObject replacement)
            {
                throw new InvalidDataException("A root JSON patch value must be an object.");
            }
            return (JObject)replacement.DeepClone();
        }

        private static JToken ResolveParent(JObject root, string[] parts, string path)
        {
            JToken current = root;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                if (current is JObject objectToken)
                {
                    current = objectToken[parts[index]]
                              ?? throw new InvalidDataException(
                                  $"JSON patch path '{path}' has no object member '{parts[index]}'.");
                    continue;
                }
                if (current is JArray arrayToken)
                {
                    var arrayIndex = RequireArrayIndex(parts[index], arrayToken.Count, allowEnd: false, path);
                    current = arrayToken[arrayIndex];
                    continue;
                }
                throw new InvalidDataException($"JSON patch path '{path}' traverses a scalar value.");
            }
            return current;
        }

        private static void Add(JToken parent, string leaf, JToken value, string path)
        {
            if (parent is JObject objectToken)
            {
                objectToken[leaf] = value.DeepClone();
                return;
            }
            if (parent is JArray arrayToken)
            {
                if (leaf == "-")
                {
                    arrayToken.Add(value.DeepClone());
                    return;
                }
                var index = RequireArrayIndex(leaf, arrayToken.Count, allowEnd: true, path);
                arrayToken.Insert(index, value.DeepClone());
                return;
            }
            throw new InvalidDataException($"JSON patch add path '{path}' targets a scalar parent.");
        }

        private static void Set(JToken parent, string leaf, JToken value, string path)
        {
            if (parent is JObject objectToken)
            {
                objectToken[leaf] = value.DeepClone();
                return;
            }
            if (parent is JArray arrayToken)
            {
                var index = RequireArrayIndex(leaf, arrayToken.Count, allowEnd: false, path);
                arrayToken[index] = value.DeepClone();
                return;
            }
            throw new InvalidDataException($"JSON patch set path '{path}' targets a scalar parent.");
        }

        private static void Remove(JToken parent, string leaf, string path)
        {
            if (parent is JObject objectToken)
            {
                if (!objectToken.Remove(leaf))
                {
                    throw new InvalidDataException($"JSON patch remove path '{path}' does not exist.");
                }
                return;
            }
            if (parent is JArray arrayToken)
            {
                var index = RequireArrayIndex(leaf, arrayToken.Count, allowEnd: false, path);
                arrayToken.RemoveAt(index);
                return;
            }
            throw new InvalidDataException($"JSON patch remove path '{path}' targets a scalar parent.");
        }

        private static JToken RequireValue(JObject patch, int patchIndex)
        {
            if (!patch.TryGetValue("value", StringComparison.Ordinal, out var value))
            {
                throw new InvalidDataException($"JSON patch at index {patchIndex} has no value.");
            }
            return value;
        }

        private static int RequireArrayIndex(string value, int count, bool allowEnd, string path)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                || result < 0
                || result > count
                || (!allowEnd && result == count))
            {
                throw new InvalidDataException($"JSON patch path '{path}' has invalid array index '{value}'.");
            }
            return result;
        }

        private static int RequirePointerArrayIndex(string value, int count, string pointer)
        {
            if (value.Length == 0
                || value.Length > 1 && value[0] == '0'
                || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                || result < 0
                || result >= count)
            {
                throw new InvalidDataException(
                    $"JSON pointer '{pointer}' has invalid array index '{value}'.");
            }
            return result;
        }

        private static string DecodePointerToken(string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '~')
                {
                    continue;
                }
                if (index + 1 >= value.Length || value[index + 1] != '0' && value[index + 1] != '1')
                {
                    throw new InvalidDataException($"JSON pointer token '{value}' contains an invalid escape.");
                }
                index++;
            }
            return value.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }
    }
}
