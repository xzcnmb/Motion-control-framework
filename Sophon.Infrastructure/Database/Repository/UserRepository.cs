using Sophon.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Sophon.Infrastructure
{
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class UserRepository : RepositoryBase<User>, IUserRepository
    {
        public UserRepository(DbContext dbContext) : base(dbContext)
        {
        }

        public Task<User> GetUserByName(string name)
        {
            return QuerySingleAsync(u => u.UserName == name);
        }

        public Task<List<string>> GetAllUserNames()
        {
            return QueryAllAsync().ContinueWith(t => t.Result.Select(u => u.UserName).ToList());
        }

        public string GetPasswordByUserName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "未登录")
            {
                return null;
            }

            return _dbContext.Db.Queryable<User>()
                .Where(u => u.UserName == name)
                .Select(u => u.Password)
                .First();
        }

        public bool VerifyPassword(string name, string password, out bool migratedLegacyPassword)
        {
            migratedLegacyPassword = false;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password)) return false;

            var user = _dbContext.Db.Queryable<User>().First(u => u.UserName == name);
            if (user == null || string.IsNullOrEmpty(user.Password)) return false;

            if (user.Password.StartsWith("pbkdf2$", StringComparison.Ordinal))
            {
                return PasswordHasher.Verify(password, user.Password);
            }

            // 兼容旧数据库：仅在一次成功登录后立即升级为哈希。
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(user.Password),
                    System.Text.Encoding.UTF8.GetBytes(password)))
            {
                return false;
            }

            user.Password = PasswordHasher.Hash(password);
            _dbContext.Db.Updateable(user).ExecuteCommand();
            migratedLegacyPassword = true;
            return true;
        }

        public UserLevel GetLevelByUserName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "未登录")
            {
                return UserLevel.None;
            }

            return _dbContext.Db.Queryable<User>()
                .Where(u => u.UserName == name)
                .Select(u => u.UserLevel)
                .First();
        }

        public bool ChangePassword(string name, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(newPassword)) return false;
            string encoded = PasswordHasher.Hash(newPassword);
            return _dbContext.Db.Updateable<User>()
                                .SetColumns(u => u.Password == encoded)
                                .Where(u => u.UserName == name)
                                .ExecuteCommand() > 0;
        }

        public bool DeleteUser(string name)
        {
            return _dbContext.Db.Deleteable<User>().Where(u => u.UserName == name).ExecuteCommand() > 0;
        }
    }
}