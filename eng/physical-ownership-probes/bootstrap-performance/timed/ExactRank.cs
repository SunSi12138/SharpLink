internal static class ExactRank {
    public static int Index(int count,int numerator,int denominator) {
        if(count<=0 || numerator<=0 || denominator<=0 || numerator>denominator)throw new System.ArgumentOutOfRangeException();
        return checked((int)(((long)count*numerator+denominator-1)/denominator-1));
    }
}
