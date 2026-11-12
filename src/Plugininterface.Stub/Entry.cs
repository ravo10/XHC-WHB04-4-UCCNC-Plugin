using System;
using System.Collections.Generic;

// Signatures copied from the MemberRefs in the stock UCCNC plugins
// (XHC-HB04_pendant.dll, UCR200Plugin.dll, UCR201Plugin.dll).
// Bodies are never executed in UCCNC: it supplies the real implementation and the plugin
// binds to it by name. 'virtual' only lets tools/Screenshots substitute demo data.
namespace Plugininterface
{
    public class Entry
    {
        public class Pluginproperties
        {
            public string author;
            public string pluginname;
            public string pluginversion;
        }

        // A struct in the real Plugininterface.dll (the stock plugins encode it as a value type).
        // Declaring it as a class causes "TypeLoadException ... value type mismatch" at runtime.
        public struct Functionproperties
        {
            public int functioncode;
            public string functiondescription;
        }

        public virtual int JogOnSpeed(int axis, bool direction, double speedPercent) { throw new NotSupportedException(); }
        public virtual int MPGJogOff(int axis) { throw new NotSupportedException(); }
        public virtual int AddLinearMoveRel(int axis, double distance, int count, double speedPercent, bool direction) { throw new NotSupportedException(); }
        public virtual bool GetLED(int number) { throw new NotSupportedException(); }
        public virtual void Callbutton(int code) { throw new NotSupportedException(); }
        public virtual void Code(string code) { throw new NotSupportedException(); }
        public virtual string Getfield(bool isAS3, int number) { throw new NotSupportedException(); }
        public virtual double Getfielddouble(bool isAS3, int number) { throw new NotSupportedException(); }
        public virtual void Setfield(bool isAS3, double value, int number) { throw new NotSupportedException(); }
        public virtual void Validatefield(bool isAS3, int number) { throw new NotSupportedException(); }
        public virtual double GetXpos() { throw new NotSupportedException(); }
        public virtual double GetYpos() { throw new NotSupportedException(); }
        public virtual double GetZpos() { throw new NotSupportedException(); }
        public virtual double GetApos() { throw new NotSupportedException(); }
        public virtual double GetBpos() { throw new NotSupportedException(); }
        public virtual double GetCpos() { throw new NotSupportedException(); }
        public virtual double GetXmachpos() { throw new NotSupportedException(); }
        public virtual double GetYmachpos() { throw new NotSupportedException(); }
        public virtual double GetZmachpos() { throw new NotSupportedException(); }
        public virtual double GetAmachpos() { throw new NotSupportedException(); }
        public virtual double GetBmachpos() { throw new NotSupportedException(); }
        public virtual double GetCmachpos() { throw new NotSupportedException(); }
        public virtual List<Functionproperties> Getbuttonsdescriptions() { throw new NotSupportedException(); }
        public virtual string Readkey(string section, string key, string defaultValue) { throw new NotSupportedException(); }
        public virtual void Writekey(string section, string key, string value) { throw new NotSupportedException(); }
    }
}
