using System;
using System.IO;

namespace Common
{
    public static class PathResolver
    {
        public static string GetAbsolutePath(string relativeOrAbsolutePath)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
            {
                throw new ArgumentException("路径不能为空", nameof(relativeOrAbsolutePath));
            }

            if (Path.IsPathRooted(relativeOrAbsolutePath))
            {
                return Path.GetFullPath(relativeOrAbsolutePath);
            }

            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativeOrAbsolutePath));
        }

        public static string GetRuntimeDataDirectory()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData");
        }
    }
}
