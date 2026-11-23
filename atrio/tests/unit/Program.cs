namespace AtrioTests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            return new NUnitLite.AutoRun(typeof(Program).Assembly).Execute(args);
        }
    }
}
