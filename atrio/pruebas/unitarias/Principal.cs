namespace AtrioTests
{
    public static class Principal
    {
        public static int Main(string[] args)
        {
            return new NUnitLite.AutoRun(typeof(Principal).Assembly).Execute(args);
        }
    }
}
