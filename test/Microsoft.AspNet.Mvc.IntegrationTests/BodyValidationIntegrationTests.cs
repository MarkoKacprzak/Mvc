﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.Mvc.ModelBinding;
using Microsoft.Framework.DependencyInjection;
using Xunit;

namespace Microsoft.AspNet.Mvc.IntegrationTests
{
    public class BodyValidationIntegrationTests
    {
        private class Person
        {
            [FromBody]
            [Required]
            public Address Address { get; set; }
        }

        private class Address
        {
            public string Street { get; set; }
        }

        [Fact]
        public async Task FromBodyAndRequiredOnProperty_EmptyBody_AddsModelStateError()
        {
            // Arrange
            var argumentBinder = ModelBindingTestHelper.GetArgumentBinder();
            var parameter = new ParameterDescriptor()
            {
                Name = "Parameter1",
                BindingInfo = new BindingInfo()
                {
                    BinderModelName = "CustomParameter",
                },
                ParameterType = typeof(Person)
            };

            var operationContext = ModelBindingTestHelper.GetOperationBindingContext();
            var httpContext = operationContext.HttpContext;

            ConfigureHttpRequest(httpContext.Request, string.Empty);
            var modelState = new ModelStateDictionary();

            // Act
            var modelBindingResult = await argumentBinder.BindModelAsync(parameter, modelState, operationContext);

            // Assert
            Assert.NotNull(modelBindingResult);
            Assert.True(modelBindingResult.IsModelSet);
            var boundPerson = Assert.IsType<Person>(modelBindingResult.Model);
            Assert.NotNull(boundPerson);
            var key = Assert.Single(modelState.Keys);
            Assert.Equal("CustomParameter.Address", key);
            Assert.False(modelState.IsValid);
            Assert.Equal("The Address field is required.", modelState[key].Errors.Single().ErrorMessage);
        }

        [Fact]
        public async Task FromBodyOnActionParameter_EmptyBody_AddsModelStateError()
        {
            // Arrange
            var argumentBinder = ModelBindingTestHelper.GetArgumentBinder();
            var parameter = new ParameterDescriptor()
            {
                Name = "Parameter1",
                BindingInfo = new BindingInfo()
                {
                    BinderModelName = "CustomParameter",
                    BindingSource = BindingSource.Body
                },
                ParameterType = typeof(Person)
            };

            var operationContext = ModelBindingTestHelper.GetOperationBindingContext();
            var httpContext = operationContext.HttpContext;

            ConfigureHttpRequest(httpContext.Request, "{ \"Id\":1234 }");
            var modelState = new ModelStateDictionary();

            // Act
            var modelBindingResult = await argumentBinder.BindModelAsync(parameter, modelState, operationContext);

            // Assert
            Assert.NotNull(modelBindingResult);
            Assert.True(modelBindingResult.IsModelSet);
            var boundPerson = Assert.IsType<Person>(modelBindingResult.Model);
            Assert.NotNull(boundPerson);
            var key = Assert.Single(modelState.Keys);
            Assert.Equal("Address", key);
            Assert.False(modelState.IsValid);
            Assert.Equal("The Address field is required.", modelState[key].Errors.Single().ErrorMessage);
        }

        private class Person4
        {
            [FromBody]
            [Required]
            public int Address { get; set; }
        }

        [Fact]
        public async Task FromBodyAndRequiredOnValueTypeProperty_EmptyBody_AddsModelStateError()
        {
            // Arrange
            var argumentBinder = ModelBindingTestHelper.GetArgumentBinder();
            var parameter = new ParameterDescriptor()
            {
                Name = "Parameter1",
                BindingInfo = new BindingInfo()
                {
                    BinderModelName = "CustomParameter",
                },
                ParameterType = typeof(Person4)
            };

            var operationContext = ModelBindingTestHelper.GetOperationBindingContext();
            var httpContext = operationContext.HttpContext;
            ConfigureHttpRequest(httpContext.Request, string.Empty);
            var actionContext = httpContext.RequestServices.GetRequiredService<IScopedInstance<ActionContext>>().Value;
            var modelState = actionContext.ModelState;

            // Act
            var modelBindingResult = await argumentBinder.BindModelAsync(parameter, modelState, operationContext);

            // Assert
            Assert.NotNull(modelBindingResult);
            Assert.True(modelBindingResult.IsModelSet);
            var boundPerson = Assert.IsType<Person4>(modelBindingResult.Model);
            Assert.NotNull(boundPerson);
            Assert.False(modelState.IsValid);

            // The error with an empty key is a bug(#2416)  in our implementation which does not append the prefix and
            // use that along with the path. The expected key here would be CustomParameter.Address.
            var key = Assert.Single(modelState.Keys, k => k == "");
            Assert.StartsWith("No JSON content found and type 'System.Int32' is not nullable.",
                modelState[""].Errors.Single().Exception.Message);
        }

        private class Person2
        {
            [FromBody]
            public Address2 Address { get; set; }
        }

        private class Address2
        {
            [Required]
            public string Street { get; set; }

            public int Zip { get; set; }
        }

        [Theory]
        [InlineData("{ \"Zip\" : 123 }")]
        [InlineData("{}")]
        public async Task FromBodyOnTopLevelProperty_RequiredOnSubProperty_AddsModelStateError(string inputText)
        {
            // Arrange
            var argumentBinder = ModelBindingTestHelper.GetArgumentBinder();
            var parameter = new ParameterDescriptor()
            {
                BindingInfo = new BindingInfo()
                {
                    BinderModelName = "CustomParameter",
                },
                ParameterType = typeof(Person2)
            };

            var operationContext = ModelBindingTestHelper.GetOperationBindingContext();
            var httpContext = operationContext.HttpContext;
            ConfigureHttpRequest(httpContext.Request, inputText);
            var modelState = new ModelStateDictionary();

            // Act
            var modelBindingResult = await argumentBinder.BindModelAsync(parameter, modelState, operationContext);

            // Assert
            Assert.NotNull(modelBindingResult);
            Assert.True(modelBindingResult.IsModelSet);
            var boundPerson = Assert.IsType<Person2>(modelBindingResult.Model);
            Assert.NotNull(boundPerson);
            Assert.False(modelState.IsValid);
            var zip = Assert.Single(modelState.Keys, k => k == "CustomParameter.Address.Zip");
            Assert.Equal(ModelValidationState.Valid, modelState[zip].ValidationState);

            var street = Assert.Single(modelState.Keys, k => k == "CustomParameter.Address.Street");
            Assert.Equal(ModelValidationState.Invalid, modelState[street].ValidationState);
            Assert.Equal("The Street field is required.", modelState[street].Errors.Single().ErrorMessage);
        }

        private class Person3
        {
            [FromBody]
            public Address3 Address { get; set; }
        }

        private class Address3
        {
            public string Street { get; set; }

            [Required]
            public int Zip { get; set; }
        }

        [Theory]
        [InlineData("{ \"Street\" : \"someStreet\" }")]
        [InlineData("{}")]
        public async Task FromBodyOnProperty_RequiredOnValueTypeSubProperty_AddsModelStateError(string inputText)
        {
            // Arrange
            var argumentBinder = ModelBindingTestHelper.GetArgumentBinder();
            var parameter = new ParameterDescriptor()
            {
                BindingInfo = new BindingInfo()
                {
                    BinderModelName = "CustomParameter",
                },
                ParameterType = typeof(Person3)
            };

            var operationContext = ModelBindingTestHelper.GetOperationBindingContext();
            var httpContext = operationContext.HttpContext;
            ConfigureHttpRequest(httpContext.Request, inputText);
            var actionContext = httpContext.RequestServices.GetRequiredService<IScopedInstance<ActionContext>>().Value;
            var modelState = actionContext.ModelState;

            // Act
            var modelBindingResult = await argumentBinder.BindModelAsync(parameter, modelState, operationContext);

            // Assert
            Assert.NotNull(modelBindingResult);
            Assert.True(modelBindingResult.IsModelSet);
            var boundPerson = Assert.IsType<Person3>(modelBindingResult.Model);
            Assert.NotNull(boundPerson);
            Assert.False(modelState.IsValid);
            var street = Assert.Single(modelState.Keys, k => k == "CustomParameter.Address.Street");
            Assert.Equal(ModelValidationState.Valid, modelState[street].ValidationState);

            // The error with an empty key is a bug(#2416) in our implementation which does not append the prefix and
            // use that along with the path. The expected key here would be Address.
            var zip = Assert.Single(modelState.Keys, k => k == "CustomParameter.Address.Zip");
            Assert.Equal(ModelValidationState.Valid, modelState[zip].ValidationState);
            Assert.StartsWith(
                "Required property 'Zip' not found in JSON. Path ''",
                modelState[""].Errors.Single().Exception.Message);
        }

  
        private static void ConfigureHttpRequest(HttpRequest request, string jsonContent)
        {
            request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonContent));
            request.ContentType = "application/json";
        }
    }
}