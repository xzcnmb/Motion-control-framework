using Sophon.Common;
using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.IO;

namespace Common
{
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class ConfigManagerFactory : IConfigManagerFactory
    {
        private readonly ConcurrentDictionary<string, IConfigManager> configManagercache = new ConcurrentDictionary<string, IConfigManager>();

        private readonly string _configPath = ConfigurationManager.AppSettings["ConfigPath"];
        private readonly object _serializerLock = new();

        public IConfigManager CreateConfigManager(ConfigType type, string filename, string secondPath = "")
        {
            IConfigSerializer serializer;
            lock (_serializerLock)
            {
                serializer = CreateSerializer(type);
            }

            string path = string.IsNullOrWhiteSpace(_configPath)
                ? Path.Combine(PathResolver.GetRuntimeDataDirectory(), "config")
                : PathResolver.GetAbsolutePath(_configPath);

            string cacheKey = $"{type}:{secondPath}:{filename}";
            string fullPath = Path.Combine(path, secondPath, filename + "." + type);
            return configManagercache.GetOrAdd(cacheKey, _ => new ConfigManager(serializer, fullPath));
        }

        public IConfigSerializer CreateSerializer(ConfigType type)
        {
            switch (type)
            {
                case ConfigType.json:
                    return new JsonConfigSerializer();

                case ConfigType.xml:
                    return new XmlConfigSerializer();

                case ConfigType.ini:
                    return new IniConfigSerializer();

                default:
                    throw new NotSupportedException($"暂未支持{type}格式");
            }
        }
    }
}