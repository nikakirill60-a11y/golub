using System;
namespace BlitzLegacy.Core.Battle
{
    /// <summary>DAVA's 57x57 two-octave force-noise lookup, not Unity's unrelated Perlin field.
    /// Reference: typicalMoves/dava.engine Sources/Internal/Math/Noise.cpp (GLM-derived algorithm).
    /// The original indexes by particle address; a stable spawn index provides equivalent independent samples.</summary>
    public static class DavaNoise
    {
        private static readonly float[,,] Table=Build();
        private static float Floor(float x){return (float)Math.Floor(x);}
        private static float Frac(float x){return x-(float)Math.Truncate(x);}
        private static float Permute(float x){return ((x*34+1)*x)%289;}
        private static float Fade(float x){return x*x*(3-2*x);}
        private static float Lerp(float a,float b,float t){return a+(b-a)*t;}
        private static float Gradient(float px,float py,float fx,float fy,float wrap)
        {
            px=(px%wrap)%289;py=(py%wrap)%289;
            var hash=Permute(Permute(px+py));var gx=2*Math.Abs(Frac(hash/41))-1;var gy=Math.Abs(gx)-.5f;gx-=Floor(gx+.5f);
            var norm=1.79284291400159f-.85373472095314f*(gx*gx+gy*gy);return (gx*fx+gy*fy)*norm;
        }
        public static float Perlin(float x,float y,float wrap)
        {
            var px=Floor(x);var py=Floor(y);var fx=Math.Abs(Frac(x));var fy=Math.Abs(Frac(y));
            var n00=Gradient(px,py,fx,fy,wrap);var n10=Gradient(px+1,py,fx-1,fy,wrap);var n01=Gradient(px,py+1,fx,fy-1,wrap);var n11=Gradient(px+1,py+1,fx-1,fy-1,wrap);
            return Lerp(Lerp(n00,n10,Fade(fx)),Lerp(n01,n11,Fade(fx)),Fade(fy))*2.3f;
        }
        private static float[,,] Build()
        {
            var values=new float[57,57,3];var offsets=new[]{new[]{0f,0f},new[]{123.4f,129845.6f},new[]{-9519f,9051f}};
            for(var x=0;x<57;x++)for(var y=0;y<57;y++)for(var channel=0;channel<3;channel++)for(var octave=0;octave<2;octave++)values[x,y,channel]+=Perlin(x*2f/56+offsets[channel][0],y*2f/56+offsets[channel][1],8*(1<<octave))*(octave==0?1:.5f);
            return values;
        }
        public static void Sample(float overLife,float frequency,int spawnIndex,out float x,out float y,out float z)
        {
            var index=((spawnIndex%57)+57)%57;var at=overLife*57*frequency+index;var first=(int)Math.Truncate(at);var fraction=at-first;first=((first%57)+57)%57;var next=(first+1)%57;
            x=Lerp(Table[first,index,0],Table[next,index,0],fraction);
            y=Lerp(Table[first,index,1],Table[next,index,1],fraction);
            z=Lerp(Table[first,index,2],Table[next,index,2],fraction);
        }
        public static float[] Sample(float overLife,float frequency,int spawnIndex)
        { Sample(overLife,frequency,spawnIndex,out var x,out var y,out var z);return new[]{x,y,z}; }
    }
}
