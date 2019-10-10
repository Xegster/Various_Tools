using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;

namespace Util
{
	public class SMTPEmailer
	{
		private const string GLISMTPIP = "smtp.gaminglabs.net";

		public static List<MailAddress> ToGLI = new List<MailAddress>(new MailAddress[] {
						new MailAddress("GLI@gaminglabs.com")});

		public static MailAddress ToEvoUser = new MailAddress("EvoUser@gaminglabs.com");

		public static List<MailAddress> ListEvoUser = new List<MailAddress>(new MailAddress[]
			{
				new MailAddress("EvoUser@gaminglabs.com")
			});

		public static List<MailAddress> ToProtocol = new List<MailAddress>(new MailAddress[] {
						new MailAddress("ProtocolTeam@gaminglabs.com")});

		public static List<MailAddress> ToEngineers = new List<MailAddress>(new MailAddress[] {
						new MailAddress("COEngineers@gaminglabs.com"),
						new MailAddress("NJGamingEngineers@gaminglabs.com"),
						new MailAddress("NJLotteryEngineers@gaminglabs.com"),
						new MailAddress("NV-Engineers@gaminglabs.com")});

		public static List<MailAddress> TeamRodger = new List<MailAddress>(new MailAddress[] {
						new MailAddress("R.Sherman@gaminglabs.com", "Rodger Sherman"),
						new MailAddress("D.Gomez@gaminglabs.com", "Dan Gomez"),
						new MailAddress("N.Yanak@gaminglabs.com", "Nick Yanak"),
						new MailAddress("K.Bystrom@gaminglabs.com", "Kale Bystrom"),
						new MailAddress("T.Lewis@gaminglabs.com", "Tim Lewis")});

		// Just so Rodger doesn't get bothered by tons of Evo only emails
		public static List<MailAddress> TeamEvo = new List<MailAddress>(new MailAddress[] {
						new MailAddress("D.Gomez@gaminglabs.com", "Dan Gomez"),
						new MailAddress("K.Bystrom@gaminglabs.com", "Kale Bystrom"),
						new MailAddress("T.Lewis@gaminglabs.com", "Tim Lewis")});

		public static List<MailAddress> TeamChris = new List<MailAddress>(new MailAddress[] {
						new MailAddress("D.Shannon@gaminglabs.com"),
						new MailAddress("S.Marlin@gaminglabs.com"),
						new MailAddress("Q.Mai@gaminglabs.com")});

		public static List<MailAddress> SWManagers = new List<MailAddress>(new MailAddress[] {
						new MailAddress("C.VanEmmerik@gaminglabs.com", "Chris Van Emmerik" ),
						new MailAddress("Z.Hollis@gaminglabs.com", "Zack Hollis")});

		public static MailAddress Zack = new MailAddress("Z.Hollis@gaminglabs.com", "Zack Hollis");
		public static MailAddress Rodger = new MailAddress("R.Sherman@gaminglabs.com", "Rodger Sherman");

		/// <summary>
		/// All members including Managers
		/// </summary>
		public static List<MailAddress> SWTeam = TeamChris.Concat(TeamRodger).Concat(SWManagers).ToList();

		/// <summary>
		/// 
		/// </summary>
		/// <param name="To"></param>
		/// <param name="CC"></param>
		/// <param name="From"></param>
		/// <param name="ReplyTo">Sets the reply-to for the email. Only available in .net 4.0+</param>
		/// <param name="Subject"></param>
		/// <param name="Body"></param>
		/// <param name="HTML"></param>
		/// <param name="priority"></param>
		/// <returns></returns>
		public static bool Email(
			List<MailAddress> To,
			List<MailAddress> CC,
			MailAddress From,
			List<MailAddress> ReplyTo,
			string Subject,
			string Body,
			bool HTML,
			MailPriority priority,
			AlternateView avHtml = null
			/*,string Attachment_Base64*/)
		{
			if (From == null || To == null || To.Count == 0) return false;

			SmtpClient smtpClient = new System.Net.Mail.SmtpClient(GLISMTPIP);
			MailMessage message = new MailMessage
			{

				//var mailAddress = new MailAddress("GLIMobile@gaminglabs.com", "GLIMobile");
				From = From
			};

			//Looks like hte ReplyToList is .net 4.0 only
#if !ERESULTS
			if (ReplyTo != null)
				foreach (MailAddress ma in ReplyTo)
					message.ReplyToList.Add(ma);
#endif
			foreach (MailAddress ma in To)
				message.To.Add(ma);

			if (CC != null)
				foreach (var c in CC)
					message.CC.Add(c);

			message.Subject = Subject;
			message.IsBodyHtml = HTML;
			//Attachment attachment = new Attachment((Stream)new MemoryStream(Convert.FromBase64String(Attachment_Base64)), docname);
			//message.Attachments.Add(attachment);
			message.Body = Body;

			message.Priority = priority;

			try
			{
				smtpClient.Send(message);
			}
			catch
			{
				return false;
			}
			return true;
		}



		public static void Email(string To, string CC, string From, string ReplyTo, string Subject, string Body, bool IsHTML, bool urgent)
		{

			Email(
				new List<MailAddress>(new MailAddress[] { new MailAddress(To) }),
				(!string.IsNullOrEmpty(CC) ? new List<MailAddress>(new MailAddress[] { new MailAddress(CC) }) : new List<MailAddress>()),
				new MailAddress(From),
				(!string.IsNullOrEmpty(ReplyTo) ? new List<MailAddress>(new MailAddress[] { new MailAddress(ReplyTo) }) : new List<MailAddress>()),
				Subject,
				Body,
				IsHTML,
				urgent ? MailPriority.High : MailPriority.Normal);
			//SmtpClient MailClient = new SmtpClient("owa.gaminglabs.com");
			////MailClient.EnableSsl = tr;
			//MailMessage Msg = new MailMessage();
			//Msg.From = new MailAddress(From);
			//Msg.To.Add(new MailAddress(To));
			//Msg.Subject = Subject;
			//Msg.Body = Body;

			//try
			//{
			//    MailClient.Send(Msg);
			//}
			//catch (Exception ex)
			//{
			//    string fuck = ex.Message;
			//}

			////base64data = base64data.Replace("_", "/");
			////base64data = base64data.Replace("-", "+");
			////byte[] buffer = Convert.FromBase64String(base64data);
			//SmtpClient smtpClient = new System.Net.Mail.SmtpClient("137.236.77.59");
			//MailMessage message = new MailMessage();
			//var mailAddress = new MailAddress("GLIMobile@gaminglabs.com", "GLIMobile");
			////smtpClient.Host = "localhost";
			////smtpClient.Port = 25;
			//message.From = mailAddress;
			//message.To.Add("R.Sherman@gaminglabs.com");//"askthegaminglab@gaminglabs.com");
			//message.Subject = "Ask GLI Question from GLI Mobile";
			//message.IsBodyHtml = false;
			////Attachment attachment = new Attachment((Stream)new MemoryStream(buffer), docname);
			////message.Attachments.Add(attachment);
			//message.Body = "Ask GLI question via GLI Mobile \n\n";

			//smtpClient.Send(message);

		}
		/*
		 * base64data = base64data.Replace("_", "/");
			base64data = base64data.Replace("-", "+");
			byte[] buffer = Convert.FromBase64String(base64data);
			SmtpClient smtpClient = new System.Net.Mail.SmtpClient("137.236.77.59");
			MailMessage message = new MailMessage();
			var mailAddress = new MailAddress("GLIMobile@gaminglabs.com", "GLIMobile");
			//smtpClient.Host = "localhost";
			//smtpClient.Port = 25;
			message.From = mailAddress;
			message.To.Add("D.Gomez@gaminglabs.com");//"askthegaminglab@gaminglabs.com");
			message.Subject = "Ask GLI Question from GLI Mobile";
			message.IsBodyHtml = false;
			Attachment attachment = new Attachment((Stream)new MemoryStream(buffer), docname);
			message.Attachments.Add(attachment);
			message.Body = "Ask GLI question via GLI Mobile \n\n";
			MailMessage mailMessage1 = message;
			string str1 = mailMessage1.Body + contact;
			mailMessage1.Body = str1;
			MailMessage mailMessage2 = message;
			string str2 = mailMessage2.Body + "\n\nQuestion:\n";
			mailMessage2.Body = str2;
			MailMessage mailMessage3 = message;
			string str3 = mailMessage3.Body + question;
			mailMessage3.Body = str3;
			smtpClient.Send(message);
			return true;
		 * */

	}
}
