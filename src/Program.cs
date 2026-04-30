using PortableMSVC;

if (FakeVsWhere.IsVsWhereProcess())
{
	if (PortableSetupRunner.IsSetupCommand(args))
	{
		return PortableSetupRunner.Run(args);
	}

	return FakeVsWhere.Run(args);
}

return await Cli.RunAsync(args);
