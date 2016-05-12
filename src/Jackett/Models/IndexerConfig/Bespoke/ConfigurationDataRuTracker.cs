using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jackett.Models.IndexerConfig.Bespoke
{
    public class ConfigurationDataRuTracker : ConfigurationData
    {
        [JsonProperty]
        public StringItem Username { get; private set; }
        public StringItem Password { get; private set; }

        public ImageItem CaptchaImage { get; private set; }
        public StringItem CaptchaText { get; private set; }
        public HiddenItem CaptchaCookie { get; private set; }

        public StringItem Url { get; private set; }
        public HiddenItem CaptchaSID { get; private set; }
        public HiddenItem CaptchaTextFieldName { get; private set; }

        public ConfigurationDataRuTracker()
        {
        }

        public ConfigurationDataRuTracker(string defaultUrl)
        {

            Username = new StringItem { Name = "Username" };
            Password = new StringItem { Name = "Password" };

            CaptchaImage = new ImageItem { Name = "Captcha Image (If no image don`t fill captcha text)" };
            CaptchaText = new StringItem { Name = "Captcha Text" };
            CaptchaCookie = new HiddenItem("") { Name = "Captcha Cookie"};

            Url = new StringItem { Name = "Url", Value = defaultUrl };
            CaptchaSID = new HiddenItem { Name = "cap_sid" };
            CaptchaTextFieldName = new HiddenItem { Name = "cap_code"};
        }
    }
}
