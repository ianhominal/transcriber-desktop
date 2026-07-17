using Xunit;

// System.Windows.Application es un singleton por AppDomain: si dos tests STA lo crean en paralelo
// (colecciones distintas corriendo en threads distintos), se pisan. Como todos los tests de este
// proyecto comparten la misma Application vía UiTestApplication.EnsureCreated(), desactivamos la
// paralelización para el assembly entero.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
