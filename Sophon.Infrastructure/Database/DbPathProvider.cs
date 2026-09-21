using Common;
using System;
using System.Configuration;
using System.IO;

namespace Sophon.Infrastructure
{
    /// <summary>
    /// 数据库路径解析：配置缺失时回退到默认本地路径，保证开箱即用。
    /// （原版依赖 App.config，配置缺失时连接串为空 → SQLite 临时库 → 建表丢失）
    /// </summary>
    public static class DbPathProvider
    {
        /// <summary>解析后的数据库文件绝对路径。</summary>
        public static string GetDatabaseFilePath()
        {
            var path = ConfigurationManager.AppSettings["DatabaseFilePath"];
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "sophon.db");
            }
            return PathResolver.GetAbsolutePath(path);
        }

        /// <summary>SQLite 连接串。</summary>
        public static string BuildConnectionString() => "Data Source = " + GetDatabaseFilePath() + ";";
    }
}