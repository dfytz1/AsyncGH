using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace AsyncGH;

public class AsyncGHInfo : GH_AssemblyInfo
{
    public override string Name => "AsyncGH";
    public override Bitmap? Icon => null;
    public override string Description =>
        "Keeps the Grasshopper UI responsive during computation. " +
        "Navigate the canvas and save definitions while GH is solving.";
    public override Guid Id => new("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
    public override string AuthorName => "AsyncGH";
    public override string AuthorContact => "";
    public override string Version => "1.4.1";
}
