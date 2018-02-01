﻿using JetBrains.Annotations;
using Nancy;
using Nancy.TinyIoc;
using Newtonsoft.Json;

namespace HidCerberus.Srv.NancyFx
{
    [UsedImplicitly]
    public class Bootstrapper : DefaultNancyBootstrapper
    {
        protected override void ConfigureApplicationContainer(TinyIoCContainer container)
        {
            base.ConfigureApplicationContainer(container);

            container.Register<JsonSerializer, CustomJsonSerializer>();
        }
    }
}