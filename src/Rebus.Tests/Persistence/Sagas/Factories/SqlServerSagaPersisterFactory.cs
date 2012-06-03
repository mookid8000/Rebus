using System.Data.SqlClient;
using Rebus.Persistence.SqlServer;
using log4net.Config;

namespace Rebus.Tests.Persistence.Sagas.Factories
{
    public class SqlServerSagaPersisterFactory : ISagaPersisterFactory
    {
        static SqlServerSagaPersisterFactory()
        {
            XmlConfigurator.Configure();
        }

        public IStoreSagaData CreatePersister()
        {
            DeleteRows("sagas");
            DeleteRows("saga_index");
            return new SqlServerSagaPersister(ConnectionStrings.SqlServer, "saga_index", "sagas");
        }

        public void Dispose()
        {
        }

        protected void DeleteRows(string tableName)
        {
            ExecuteCommand("delete from " + tableName);
        }

        static void ExecuteCommand(string commandText)
        {
            using (var conn = new SqlConnection(ConnectionStrings.SqlServer))
            {
                conn.Open();

                using (var command = conn.CreateCommand())
                {
                    command.CommandText = commandText;
                    command.ExecuteNonQuery();
                }
            }
        }
    }
}