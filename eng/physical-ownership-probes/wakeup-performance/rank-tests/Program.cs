foreach(int count in new[]{1,2,10,100,1000,2000,10000,1000000,int.MaxValue}) {
    foreach(var (num,den) in new[]{(1,2),(19,20),(99,100),(999,1000),(1,1)}) {
        int index=ExactRank.Index(count,num,den);
        if(index<0 || index>=count || (long)index*den>=(long)count*num || (long)(index+1)*den<(long)count*num)throw new Exception($"Bad exact rank {count}/{num}/{den}: {index}");
    }
}
if(ExactRank.Index(1000,999,1000)!=998 || ExactRank.Index(2000,999,1000)!=1997 || ExactRank.Index(1000000,999,1000)!=998999)throw new Exception("Exact denominator boundary failed");
foreach(var (n,num,den) in new[]{(0,1,2),(1,0,2),(1,3,2),(1,1,0)}) {
    try { ExactRank.Index(n,num,den);throw new Exception("Invalid rank accepted"); }
    catch(ArgumentOutOfRangeException) {}
}
Console.WriteLine("EXACT_RANK_BOUNDARIES_PASSED (45 valid / 4 invalid, no performance measurement)");
