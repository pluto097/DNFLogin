using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DNFLogin
{
    internal static class LoginConfig
    {
        private const string DefaultBaseUrl = "https://dnf.cv58.xyz";
        private const string DefaultWindowTitle = "地下城与勇士V1.0";
        private const string DefaultGameVersion = "1.0.0";
        private const string ConfigFileName = "login.ini";
        private const string LoginSectionName = "Login";
        private const string BaseUrlKey = "BaseUrl";
        private const string WindowTitleKey = "WindowTitle";
        private const string GameVersionKey = "GameVersion";
        private const string CredentialsSectionName = "Credentials";
        private const string AccountKey = "Account";
        private const string PasswordKey = "Password";
        private const string RememberPasswordKey = "RememberPassword";

        public static string GetBaseUrl()
        {
            var configPath = EnsureConfigFile();

            try
            {
                var ini = IniDocument.Load(configPath);

                if (ini.TryGetValue(LoginSectionName, out var section) &&
                    section.TryGetValue(BaseUrlKey, out var urlValue) &&
                    IsValidUrl(urlValue, out var normalized))
                {
                    return normalized;
                }

                WriteDefaultConfig(configPath);
                return DefaultBaseUrl;
            }
            catch
            {
                return DefaultBaseUrl;
            }
        }

        public static string GetGameVersion()
        {
            var configPath = EnsureConfigFile();

            try
            {
                var ini = IniDocument.Load(configPath);
                var loginSection = ini.GetOrAddSection(LoginSectionName);

                if (!loginSection.TryGetValue(GameVersionKey, out var version) || string.IsNullOrWhiteSpace(version))
                {
                    version = DefaultGameVersion;
                    loginSection[GameVersionKey] = version;
                    ini.WriteTo(configPath);
                }

                return version.Trim();
            }
            catch
            {
                return DefaultGameVersion;
            }
        }

        public static void SetGameVersion(string? version)
        {
            version = string.IsNullOrWhiteSpace(version) ? DefaultGameVersion : version.Trim();
            var configPath = EnsureConfigFile();

            try
            {
                var ini = IniDocument.Load(configPath);
                ini.GetOrAddSection(LoginSectionName)[GameVersionKey] = version;
                ini.WriteTo(configPath);
            }
            catch
            {
                // ignored
            }
        }

        public static string GetWindowTitle()
        {
            var configPath = EnsureConfigFile();

            try
            {
                var ini = IniDocument.Load(configPath);
                var loginSection = ini.GetOrAddSection(LoginSectionName);

                if (!loginSection.TryGetValue(WindowTitleKey, out var title) || string.IsNullOrWhiteSpace(title))
                {
                    title = DefaultWindowTitle;
                    loginSection[WindowTitleKey] = title;
                    ini.WriteTo(configPath);
                }

                return title.Trim();
            }
            catch
            {
                return DefaultWindowTitle;
            }
        }

        public static LoginCredentials GetSavedCredentials()
        {
            var configPath = EnsureConfigFile();

            try
            {
                var ini = IniDocument.Load(configPath);

                if (!ini.TryGetValue(CredentialsSectionName, out var section))
                {
                    return LoginCredentials.Empty;
                }

                section.TryGetValue(AccountKey, out var account);
                section.TryGetValue(PasswordKey, out var password);
                var remember = section.TryGetValue(RememberPasswordKey, out var rememberValue) &&
                               bool.TryParse(rememberValue, out var rememberBool) &&
                               rememberBool;

                return new LoginCredentials(account ?? string.Empty, password ?? string.Empty, remember);
            }
            catch
            {
                return LoginCredentials.Empty;
            }
        }

        public static void SaveCredentials(LoginCredentials credentials)
        {
            var configPath = EnsureConfigFile();

            try
            {
                var ini = IniDocument.Load(configPath);
                var credentialSection = ini.GetOrAddSection(CredentialsSectionName);

                if (credentials.RememberPassword &&
                    (!string.IsNullOrWhiteSpace(credentials.Account) || !string.IsNullOrWhiteSpace(credentials.Password)))
                {
                    credentialSection[AccountKey] = credentials.Account ?? string.Empty;
                    credentialSection[PasswordKey] = credentials.Password ?? string.Empty;
                    credentialSection[RememberPasswordKey] = bool.TrueString;
                }
                else
                {
                    credentialSection.Remove(AccountKey);
                    credentialSection.Remove(PasswordKey);
                    credentialSection[RememberPasswordKey] = bool.FalseString;
                }

                if (!ini.ContainsKey(LoginSectionName))
                {
                    ini.GetOrAddSection(LoginSectionName)[BaseUrlKey] = DefaultBaseUrl;
                }

                var loginSection = ini.GetOrAddSection(LoginSectionName);
                if (!loginSection.ContainsKey(WindowTitleKey))
                {
                    loginSection[WindowTitleKey] = DefaultWindowTitle;
                }

                if (!loginSection.ContainsKey(GameVersionKey))
                {
                    loginSection[GameVersionKey] = DefaultGameVersion;
                }

                ini.WriteTo(configPath);
            }
            catch
            {
                // ignored
            }
        }

        private static bool IsValidUrl(string? value, out string normalized)
        {
            normalized = DefaultBaseUrl;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var candidate = value.Trim();
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            normalized = candidate.TrimEnd('/');
            return true;
        }

        private static string EnsureConfigFile()
        {
            var preferredPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            if (TryEnsureConfigFile(preferredPath))
            {
                return preferredPath;
            }

            var fallbackDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DNFLogin");

            Directory.CreateDirectory(fallbackDirectory);
            var fallbackPath = Path.Combine(fallbackDirectory, ConfigFileName);
            TryEnsureConfigFile(fallbackPath);
            return fallbackPath;
        }

        private static bool TryEnsureConfigFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    WriteDefaultConfig(path);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteDefaultConfig(string path)
        {
            var document = new IniDocument
            {
                [LoginSectionName] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { BaseUrlKey, DefaultBaseUrl },
                    { WindowTitleKey, DefaultWindowTitle },
                    { GameVersionKey, DefaultGameVersion }
                }
            };

            document.WriteTo(path);
        }

        private sealed class IniDocument : Dictionary<string, Dictionary<string, string>>
        {
            internal IniDocument() : base(StringComparer.OrdinalIgnoreCase)
            {
            }

            public static IniDocument Load(string path)
            {
                var document = new IniDocument();
                Dictionary<string, string>? currentSection = null;

                foreach (var rawLine in File.ReadLines(path))
                {
                    var line = rawLine.Trim();

                    if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#"))
                    {
                        continue;
                    }

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        var sectionName = line.Substring(1, line.Length - 2).Trim();
                        if (!document.TryGetValue(sectionName, out currentSection))
                        {
                            currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            document[sectionName] = currentSection;
                        }

                        continue;
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separatorIndex).Trim();
                    var value = line.Substring(separatorIndex + 1).Trim();

                    currentSection ??= document.GetOrAddSection(string.Empty);
                    currentSection[key] = value;
                }

                return document;
            }

            public Dictionary<string, string> GetOrAddSection(string name)
            {
                if (!TryGetValue(name, out var section))
                {
                    section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    this[name] = section;
                }

                return section;
            }

            public void WriteTo(string path)
            {
                var lines = new List<string>
                {
                    "; DNF Login configuration"
                };

                foreach (var section in this.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(section.Key))
                    {
                        lines.Add($"[{section.Key}]");
                    }

                    foreach (var pair in section.Value)
                    {
                        lines.Add($"{pair.Key}={pair.Value}");
                    }

                    lines.Add(string.Empty);
                }

                File.WriteAllLines(path, lines);
            }
        }

        public sealed record LoginCredentials(string? Account, string? Password, bool RememberPassword)
        {
            public static LoginCredentials Empty { get; } = new(string.Empty, string.Empty, false);
        }
    }
}
