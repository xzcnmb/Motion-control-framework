using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sophon.Infrastructure
{
    public interface IUserRepository : IRepository<User>
    {
        Task<User> GetUserByName(string name);

        Task<List<string>> GetAllUserNames();

        string GetPasswordByUserName(string name);

        bool VerifyPassword(string name, string password, out bool migratedLegacyPassword);

        UserLevel GetLevelByUserName(string name);

        bool ChangePassword(string name, string newPassword);

        bool DeleteUser(string name);
    }
}