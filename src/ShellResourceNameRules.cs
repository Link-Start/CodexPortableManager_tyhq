using System;

namespace CodexPortableManager
{
    internal static class ShellResourceNameRules
    {
        internal static bool IsSafeProtocol(string value)
        {
            if (!IsCanonical(value) || value.Length > 64 || !IsAsciiLetter(value[0]))
            {
                return false;
            }
            for (int index = 1; index < value.Length; index++)
            {
                char character = value[index];
                if (!IsAsciiLetter(character) &&
                    (character < '0' || character > '9') &&
                    character != '+' && character != '-' && character != '.')
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool IsSafeExtension(string value)
        {
            if (!IsCanonical(value) || value.Length > 64 || value[0] != '.')
            {
                return false;
            }
            foreach (char character in value)
            {
                if (character == '\\' || character == '/' ||
                    character == '\0' || char.IsControl(character))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool IsSafeRegistryComponent(string value)
        {
            if (!IsCanonical(value) || value.Length > 255)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (character == '\\' || character == '/' ||
                    character == '\0' || char.IsControl(character))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool IsSafeExecutableName(string value)
        {
            return IsSafeRegistryComponent(value) &&
                value.IndexOf(':') < 0 &&
                !string.Equals(value, ".", StringComparison.Ordinal) &&
                !string.Equals(value, "..", StringComparison.Ordinal);
        }

        private static bool IsCanonical(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                string.Equals(value, value.Trim(), StringComparison.Ordinal);
        }

        private static bool IsAsciiLetter(char value)
        {
            return (value >= 'A' && value <= 'Z') ||
                (value >= 'a' && value <= 'z');
        }
    }
}
