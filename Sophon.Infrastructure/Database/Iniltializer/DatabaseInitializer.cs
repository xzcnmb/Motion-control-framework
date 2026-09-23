using Common;
using Sophon.Common;
using SqlSugar;
using System;
using System.Configuration;
using System.IO;
using System.Linq;

namespace Sophon.Infrastructure
{
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class DatabaseInitializer : IDatabaseInitializer
    {
        public DatabaseInitializer(DbContext dbContext)
        {
            _dbContext = dbContext;
        }

        private readonly DbContext _dbContext;
        private Type[] _entityTypes;

        public void Initialize()
        {
            GetAllEntities();
            InitDbContext();
            SetDefaultData();
        }

        public void InitDbContext()
        {
            string connstr = DbPathProvider.BuildConnectionString();

            string fullPath = connstr.Split('=')[1].Split(';')[0].Trim();

            string directoryPath = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            _dbContext.Db.CodeFirst.InitTables(_entityTypes);
            _dbContext._logger.Debug($"成功创建{_entityTypes.Length}张表。");
        }

        public Type[] GetAllEntities()
        {
            if (_entityTypes != null)
            {
                return _entityTypes;
            }
            var assembly = typeof(User).Assembly;
            _entityTypes = assembly.GetTypes().Where(x => typeof(IEntity).IsAssignableFrom(x) &&
                                        !x.IsAbstract && !x.IsInterface &&
                                        x.IsDefined(typeof(SugarTable), false)).ToArray();

            string names = string.Join(",", _entityTypes.Select(t => t.Name));
            _dbContext._logger.Debug($"成功获取{_entityTypes.Length}个实体类，分别为{names}。");
            return _entityTypes;
        }

        private void SetDefaultData()
        {
            // 分别为标准三级用户独立做存在性检测并按需播种，确保旧数据库也能无缝补齐缺失角色
            SeedUserIfMissing("管理员", "123", UserLevel.Admin);
            SeedUserIfMissing("工程师", "123", UserLevel.Engineer);
            SeedUserIfMissing("操作员", "123", UserLevel.Operator);
        }

        private void SeedUserIfMissing(string userName, string password, UserLevel level)
        {
            bool exists = _dbContext.Db.Queryable<User>().Any(u => u.UserName == userName);
            if (!exists)
            {
                var user = new User
                {
                    UserName = userName,
                    Password = PasswordHasher.Hash(password),
                    UserLevel = level,
                    CreateTime = DateTime.Now,
                    LatestChangeTime = DateTime.Now,
                };
                _dbContext.Db.Insertable(user).ExecuteCommand();
                _dbContext._logger.Info($"[DatabaseInitializer] 已自动播种预置用户: {userName} (权限: {level})");
            }
        }
    }
}