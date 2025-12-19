using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProxyControl.Models
{
    public class AppSettings
    {
        public bool IsAutoStart { get; set; }
        public List<ProxyItem> Proxies { get; set; } = new List<ProxyItem>();
        public AppConfig Config { get; set; } = new AppConfig();
    }
}
