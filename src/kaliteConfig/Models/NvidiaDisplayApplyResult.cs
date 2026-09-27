using System;
using System.Collections.Generic;

namespace kaliteConfig.Models
{
    public class NvidiaDisplayApplyResult
    {
        public List<string> Applied { get; } = new();
        public List<string> Failed { get; } = new();
        public bool AllSucceeded => Failed.Count == 0;
    }
}
