using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using Util;
using MailAddress = System.Net.Mail.MailAddress;
using MailPriority = System.Net.Mail.MailPriority;

namespace AutoEmailer
{
	public class Program
	{
		public enum EmailBodyFormatting
		{
			BODY,
			FULLNAME,
			TITLE,
			GLIPHONENUMBER,
			EXTENSION,
			DIRECTPHONENUMBER,


		}
		public static void Main(string[] args)
		{
			var defaultEmail = AutoEmailer.Properties.Resources.DefaultEmail;
			var test = AutoEmailer.Properties.Resources.GLI_Email_Signature;

			//SvenAttend();
			SMTPEmailer.Email(new List<MailAddress>()//To
			{
				new MailAddress("r.sherman@gaminglabs.com", "Regd0r"),
				//new MailAddress("A.Stivahtaris@gaminglabs.com", "Stiv"),
				//new MailAddress("L.Pangestu@gaminglabs.com", "Lid")
			},
			new List<MailAddress>(),//CC
									//new MailAddress("L.Shanks@gaminglabs.com", "Lachlan Shanks"),//FROM
			new MailAddress("r.sherman@gaminglabs.com", "Regd0r"),//FROM
			new List<MailAddress>()
			{
			//new MailAddress("L.Shanks@gaminglabs.com", "Lachlan Shanks")//REPLYTO
			new MailAddress("r.sherman@gaminglabs.com", "Regd0r"),//REPLYTO
			},
			"You're the best",//Subject
			test,//Body
			true, //Is Html?
			MailPriority.Normal
			);
		}

		public static void OneTimeEmail(string from, string to, string replyTo, string subject, string body, bool urgent)
		{
			SMTPEmailer.Email(to, null, from, replyTo, subject, body, true, false);
		}

		public static void GetBase64Image()
		{
			var test2 = Properties.Resources.x_graphic_side_long_version;

			MemoryStream ms = new MemoryStream();
			test2.Save(ms, ImageFormat.Png);
			byte[] byteImage = ms.ToArray();
			var SigBase64 = Convert.ToBase64String(byteImage);
		}

		public static void PleaseAttend()
		{
			var destinationEmail = "S.Klasen@gaminglabs.com";
			var destinationFirstName = "Sven Klasen ";

			string MyEmail = "r{0}.{0}s{0}h{0}e{0}r{0}m{0}a{0}n{0}@gaminglabs.com";
			string subject = "T{0}hi{0}s Sa{0}tur{0}day";
			string body = " pleas{0}e att{0}end the p{0}art{0}y.";

			List<string> Qualifiers = new List<string>()
			{
				"the Magnificent",
				"the Incredible",
				"the Enchanted",
				"the Pure",
				"the Mighty",
				"the Idolized",
				"the Adored",
				"the Unequaled",
				"the Glorious",
				"the Wise",
				"the Amazing",
				"the Accomplished",
			};


			Random r = new Random();
			for (int i = 0; i < 50; i++)
			{
				var myAdd = string.Format(MyEmail, r.Next() % 10);
				int qual = r.Next() % Qualifiers.Count;
				var myName = string.Format("Ro{0}dg{0}er {1}", r.Next() % 10, Qualifiers[qual]);
				qual = r.Next() % Qualifiers.Count;
				var name = destinationFirstName + Qualifiers[qual];

				var email = new MailAddress(destinationEmail, name);


				var sub = string.Format(subject, r.Next() % 10);
				var bod = name + ",<BR><BR>" + string.Format(body, r.Next() % 10);
				Console.WriteLine(string.Format("{0} emailed {1} at {2}. That's {3} time(s).", myName, name, DateTime.Now, i + 1));

				SMTPEmailer.Email(new List<MailAddress>() { email },
									null,
									new MailAddress(myAdd, myName),
									new List<MailAddress>() { SMTPEmailer.Rodger },
									sub,
									bod,
									true,
									MailPriority.High
									);
				var waittime = r.Next() % 60;
				Thread.Sleep(waittime * 60000);

			}
		}
	}
}
