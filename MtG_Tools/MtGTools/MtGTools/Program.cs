using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MtGTools
{
    class Program
    {
        private struct Card
        {
            public int Count;
            public string Name;
            public Card(int count, string name) { Count = count; Name = name;
        }
        static void Main(string[] args)
        {
            Console.WriteLine(string.Format("Null? - {0}; Type - {1};  Count - {2};", args == null, args == null ? "Null" : args.GetType().ToString(), args == null ? 0 : args.Length));
            Console.WriteLine(args[0]);
            var x = Console.ReadLine();
            var x2 = "";

            var Cards = GetCards(args[0]);

        }

        private static object GetCards(string filePath)
        {
            List<Card> Ret = new List<Card>();
            using (StreamReader sr = new StreamReader(filePath, true))
            {
                while (!sr.EndOfStream)
                    ret.Add(sr.ReadLine());
            }        
        }
    }
}
