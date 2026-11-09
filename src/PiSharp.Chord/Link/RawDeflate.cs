namespace PiSharp.Chord.Link;

/// <summary>Validates raw RFC 1951 termination and output size before DeflateStream allocates output.</summary>
/// <remarks>DeflateStream accepts some truncated input as EOF; the wire contract requires zlib's corrupt outcome.</remarks>
internal static class RawDeflate
{
    private static readonly int[] LengthBase=[3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258];
    private static readonly int[] LengthExtra=[0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0];
    private static readonly int[] DistanceBase=[1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577];
    private static readonly int[] DistanceExtra=[0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13];
    // RFC order, kept explicit to make audit of the dynamic header straightforward.
    private static readonly int[] CodeOrder=[16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15];
    internal static long Measure(ReadOnlyMemory<byte> bytes,long limit,CancellationToken cancellationToken=default)
    {
        var bits=new Bits(bytes); long output=0; bool final;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            final=bits.Read(1)!=0; var type=bits.Read(2);
            if(type==0)
            {
                bits.Align(); var length=bits.Read(16); var complement=bits.Read(16);
                if((length^0xffff)!=complement) throw Bad();
                bits.SkipBytes(length); output=checked(output+length);
                if(output>limit) return output;
                continue;
            }
            if(type==3) throw Bad();
            Huffman literal,distance;
            if(type==1)
            {
                var lens=new int[288];
                for(var i=0;i<288;i++) lens[i]=i<=143 ? 8 : i<=255 ? 9 : i<=279 ? 7 : 8;
                literal=new(lens); distance=new(Enumerable.Repeat(5,32).ToArray());
            }
            else
            {
                var nlit=bits.Read(5)+257; var ndist=bits.Read(5)+1; var ncode=bits.Read(4)+4;
                if(nlit>286 || ndist>32) throw Bad();
                var codeLengths=new int[19]; for(var i=0;i<ncode;i++) codeLengths[CodeOrder[i]]=bits.Read(3);
                var codeTree=new Huffman(codeLengths); var lengths=new int[nlit+ndist]; var offset=0;
                while(offset<lengths.Length)
                {
                    var code=codeTree.Decode(bits);
                    if(code<16) lengths[offset++]=code;
                    else
                    {
                        var repeat=code switch { 16=>bits.Read(2)+3,17=>bits.Read(3)+3,18=>bits.Read(7)+11,_=>throw Bad() };
                        if(repeat>lengths.Length-offset || code==16 && offset==0) throw Bad();
                        var length=code==16 ? lengths[offset-1] : 0;
                        for(var i=0;i<repeat;i++) lengths[offset++]=length;
                    }
                }
                if(lengths[256]==0) throw Bad();
                literal=new(lengths[..nlit]); distance=new(lengths[nlit..]);
            }
            var tick=0;
            while(true)
            {
                if((tick++&4095)==0) cancellationToken.ThrowIfCancellationRequested();
                var symbol=literal.Decode(bits); if(symbol==256) break;
                if(symbol<256) output++;
                else
                {
                    var index=symbol-257; if((uint)index>=LengthBase.Length) throw Bad();
                    var length=LengthBase[index]+bits.Read(LengthExtra[index]); var dist=distance.Decode(bits);
                    if((uint)dist>=DistanceBase.Length) throw Bad();
                    var back=DistanceBase[dist]+bits.Read(DistanceExtra[dist]); if(back>output) throw Bad();
                    output=checked(output+length);
                }
                if(output>limit) return output;
            }
        } while(!final);
        return output;
    }
    private static InvalidDataException Bad()=>new("Incomplete or corrupt raw DEFLATE stream.");
    private sealed class Bits(ReadOnlyMemory<byte> bytes)
    {
        private long _position;
        internal int Read(int count)
        {
            if(count<0 || _position+count>(long)bytes.Length*8) throw Bad();
            var result=0;
            for(var i=0;i<count;i++,_position++) result|=((bytes.Span[(int)(_position/8)]>>(int)(_position%8))&1)<<i;
            return result;
        }
        internal void Align()=>_position=(_position+7)&~7L;
        internal void SkipBytes(int count) { if(_position+(long)count*8>(long)bytes.Length*8) throw Bad(); _position+=(long)count*8; }
    }
    private sealed class Huffman
    {
        private readonly Dictionary<int,int> _symbols=new();
        private readonly int _max;
        internal Huffman(int[] lengths)
        {
            var counts=new int[16]; foreach(var len in lengths) { if((uint)len>15) throw Bad(); counts[len]++; _max=Math.Max(_max,len); }
            var left=1; for(var len=1;len<=15;len++) { left=(left<<1)-counts[len]; if(left<0) throw Bad(); }
            var next=new int[16]; var code=0; for(var len=1;len<=15;len++) { code=(code+(len==1 ? 0 : counts[len-1]))<<1; next[len]=code; }
            for(var symbol=0;symbol<lengths.Length;symbol++)
            {
                var len=lengths[symbol]; if(len==0) continue; var value=next[len]++; var reverse=0;
                for(var i=0;i<len;i++) { reverse=(reverse<<1)|(value&1); value>>=1; }
                _symbols[(1<<len)|reverse]=symbol;
            }
        }
        internal int Decode(Bits bits)
        {
            var value=0;
            for(var len=1;len<=_max;len++) { value|=bits.Read(1)<<(len-1); if(_symbols.TryGetValue((1<<len)|value,out var symbol)) return symbol; }
            throw Bad();
        }
    }
}
