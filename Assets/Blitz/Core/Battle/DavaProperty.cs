using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>Lossless evaluation of the 11.20 SC2 PropertyLine encoding, including Hermite keys.
    /// Colours stay in authored sRGB; converting them belongs at the render boundary.
    /// External properties multiply the value line, rather than replacing it.</summary>
    public static class DavaProperty
    {
        public static double Scalar(JsonNode value, double time, double fallback=0, IReadOnlyDictionary<string,double> variables=null)
        {
            if(value.Kind==JsonKind.Number)return value.AsDouble();
            if(value.Kind!=JsonKind.Array || value.Count==0)return fallback;
            if(value[0].AsInt()==0 && value.Count>=4 && value[1].Kind==JsonKind.String)
            {
                double input=0;
                if(variables!=null)variables.TryGetValue(value[1].AsString(),out input);
                var modifier=Scalar(value[2],input,1,variables);
                return modifier*Scalar(value[3],time,fallback,variables);
            }
            if(value[0].AsInt()==2 && value.Count>=2 && value[1].Kind==JsonKind.Array)return Curve(value[1],time,fallback);
            return fallback;
        }
        public static double Curve(JsonNode keys,double time,double fallback=0)
        {
            if(keys.Count==0)return fallback;
            if(time<=keys[0][2].AsDouble())return keys[0][1].AsDouble();
            for(var i=1;i<keys.Count;i++)
            {
                var a=keys[i-1];var b=keys[i];var t1=b[2].AsDouble();
                if(time>t1)continue;
                var span=t1-a[2].AsDouble();if(span<=0)return b[1].AsDouble();
                var u=(time-a[2].AsDouble())/span;var x=a[1].AsDouble();var y=b[1].AsDouble();
                var interpolation=a[0].AsInt();
                if(interpolation==0)return x;
                if(interpolation==2 && a.Count>=5 && b.Count>=4)
                {var u2=u*u;var u3=u2*u;return (2*u3-3*u2+1)*x+(u3-2*u2+u)*span*a[4].AsDouble()+(-2*u3+3*u2)*y+(u3-u2)*span*b[3].AsDouble();}
                return x+(y-x)*u;
            }
            return keys[keys.Count-1][1].AsDouble();
        }
        public static double Component(JsonNode value,double time,int channel,double fallback=1,IReadOnlyDictionary<string,double> variables=null)
        {
            if(value.Kind!=JsonKind.Array)return fallback;
            if(value.Count>=4 && value[0].AsInt()==0 && value[1].Kind==JsonKind.String){double input=0;if(variables!=null)variables.TryGetValue(value[1].AsString(),out input);return Component(value[2],input,channel,1,variables)*Component(value[3],time,channel,fallback,variables);}
            if(value.Count>1 && value[0].AsInt()==2 && value[1].Kind==JsonKind.Array)return Curve(value[channel+1],time,fallback);
            return value[channel].AsDouble(fallback);
        }
        public static double[] Vector(JsonNode value,double time,int dimensions,double fallback=1,IReadOnlyDictionary<string,double> variables=null)
        {
            var output=new double[dimensions];
            if(value.Kind==JsonKind.Array && value.Count>=4 && value[0].AsInt()==0 && value[1].Kind==JsonKind.String)
            {
                double input=0;if(variables!=null)variables.TryGetValue(value[1].AsString(),out input);
                var modifiers=Vector(value[2],input,dimensions,1,variables);var baseValue=Vector(value[3],time,dimensions,fallback,variables);
                for(var i=0;i<dimensions;i++)output[i]=modifiers[i]*baseValue[i];return output;
            }
            var isCurve=value.Kind==JsonKind.Array && value.Count>1 && value[0].AsInt()==2 && value[1].Kind==JsonKind.Array;
            for(var i=0;i<dimensions;i++)output[i]=isCurve?Curve(value[i+1],time,fallback):value[i].AsDouble(fallback);
            return output;
        }
        public static double[] Colour(JsonNode value,double time,IReadOnlyDictionary<string,double> variables=null)
        {
            if(value.Kind==JsonKind.Number)
            {
                var packed=unchecked((uint)(long)value.AsDouble());
                return new[]{((packed>>24)&255)/255d,((packed>>16)&255)/255d,((packed>>8)&255)/255d,(packed&255)/255d};
            }
            return Vector(value,time,4,1,variables);
        }
    }
}
