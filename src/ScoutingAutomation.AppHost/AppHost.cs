var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
                      .WithDataVolume()
                      .WithPgAdmin();

var db = postgres.AddDatabase("scoutingdb");

builder.AddProject<Projects.ScoutingAutomation_Web>("web")
       .WithReference(db)
       .WaitFor(db);

builder.Build().Run();
