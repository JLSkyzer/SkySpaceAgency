using Unity.Properties;
using UnityEngine.UIElements;
using System;


namespace SsaUI
{
    // The status mirror of the pilots (AppPilotStatus): the pilots' controllers write their status, console
    // text and progress into a detached FullStatus made of a StatusLine, a Console and a SsaProgressBar;
    // the window's tabs copy them into the new window's controls.
    [UxmlElement]
    public partial class Console : Label
    {
        public static new readonly string ussClassName = "console";

        public Console() : base()
        {
            AddToClassList(ussClassName);
        }

        public void Set(string txt)
        {
            this.text = txt;
            this.Show(true);
        }

        public void Add(string line)
        {
            if (string.IsNullOrEmpty(text))
                text = line;
            else      
                text += "\n"+line;
            this.Show(true);
        }
    }

    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). The old UxmlTraits.Init() always applied "level" from the bag, defaulting to
    // Level.Normal - which every single <SsaUI.StatusLine> tag in the project relies on, since none
    // of them specify level="..." at all (they only ever set it later from C#, e.g. Set(text,
    // level)). Without setting it explicitly here, a fresh StatusLine would be missing its
    // "k2-status-line--normal" USS class entirely rather than having it applied - `level =
    // Level.Normal;` in the constructor reproduces the old guarantee.
    [UxmlElement]
    public partial class StatusLine : Label
    {
        public enum Level
        {
            Normal,
            Warning,
            Error
        }

        const string uss_name = "k2-status-line";

        string getUss(Level level)
        {
            return uss_name+ "--" + Enum.GetName( typeof(Level), level).ToLower();
        }

        Level _level = Level.Normal;

        [CreateProperty]
        [UxmlAttribute("level")]
        public Level level
        {
            get { return _level; }
            set
            {
                var current_uss = getUss(_level);
                RemoveFromClassList(current_uss);

                _level = value;
                current_uss = getUss(_level);
                AddToClassList(current_uss);
            }
        }

        public void Set(string text, Level level)
        {
            this.text = text;
            this.level = level;
            this.Show(true);
        }

        public StatusLine() : base()
        {
            level = Level.Normal;
            AddToClassList(uss_name);
        }
    }


}