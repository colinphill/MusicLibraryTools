using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using MetadataCaching;
using System.Threading.Tasks;
using System.Linq;

namespace MetadataDBWork
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: MetadataDBWork <database-file> <scan-root> [scan-root...] [--reset]");
                return;
            }

            Console.WriteLine(DateTime.Now);

            Console.WriteLine("Indexing...");

            string database = args[0];
            bool reset = args.Skip(1).Any(a => a.Equals("--reset", StringComparison.OrdinalIgnoreCase));
            string[] roots = args.Skip(1).Where(a => !a.Equals("--reset", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (reset)
            {
                string sqlitePath = database.StartsWith("sqlite:", StringComparison.OrdinalIgnoreCase) ? database[7..] : database;
                if (database.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--reset only supports SQLite database files.");
                File.Delete(sqlitePath);
            }

            using (var db = MetadataDatabase.OpenDatabase(database))
            {
                var res = db.IndexFiles(roots, true);
                Console.WriteLine($"Added:{res.Added} Modified:{res.Modified} Removed:{res.Removed} Unchanged:{res.Unchanged}");
                Console.WriteLine();
            }

            Console.WriteLine(DateTime.Now);
        }
    }
}
