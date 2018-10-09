using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MtGTools
{
    class Program
    {

        static void Main(string[] args)
        {
            //Console.WriteLine(string.Format("Null? - {0}; Type - {1};  Count - {2};", args == null, args == null ? "Null" : args.GetType().ToString(), args == null ? 0 : args.Length));
            //Console.WriteLine(args[0]);
            //var x = Console.ReadLine();
            //var x2 = "";
            var Words = GetGomezWords(@"C:\Users\rodger_s\Desktop\Xeg\Various_Tools_Repo\Gomez_Auto_Translator\GomezChat.txt");

            int iter = 1;
            using (StreamWriter sr = new StreamWriter("DGAT.ahk", false))
            {
                sr.WriteLine(@"#IfWinActive ahk_exe Skype.exe");

                foreach (var word in Words)
                {
                    sr.WriteLine(string.Format("::{0}::{1}", word, RandomSpanishWord));
                }
                
            }
        }

        private static Random rand = new Random();

        public static string RandomSpanishWord
        {
            get
            {
                return SpanishWords[rand.Next(SpanishWords.Count)];
            }
        }

        public static List<string> SpanishWords = new List<string>()
        {
            "Taco",
            "Sombrero",
            "Tequila",
            "Burrito",
            "Taco Taco",
            "Burrito Burrito",
            "Chimichanga",
            "Enchelada",
            "Gringo",
            "Ese",
            "Ay Caramba",
            "Ándele",
            "Chalupa",
            "Queso",
            "Salsa",
            "tu mama",
            "White Devil",
            "Racism",
            "Que",
            "Horchata",
            "Cholo",
        };

        private static List<string> GetGomezWords(string filePath)
        {
            List<string> Ret = new List<string>();
            using (StreamReader sr = new StreamReader(filePath, true))
            {
                while (!sr.EndOfStream)
                {
                    var line = sr.ReadLine();
                    if (!line.Contains("Gomez:")) continue;
                    line = line.Substring(line.IndexOf("Gomez:"));
                    line = Regex.Replace(line, @"[^\w\s]", "");
                    Ret = Ret.Union(line.Split(' ')).ToList();
                }
            }
            return Ret.Where(ret => !string.IsNullOrEmpty(ret)).ToList();
        }
    }
}
