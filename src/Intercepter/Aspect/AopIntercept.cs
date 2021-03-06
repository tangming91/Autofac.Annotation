﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AspectCore.Extensions.Reflection;
using Autofac.Annotation;
using Autofac.Aspect;
using Castle.DynamicProxy;
using  Autofac.Annotation.Util;

namespace Autofac.Aspect
{
    /// <summary>
    /// AOP拦截器方法Attribute的缓存
    /// 在DI容器build的时候会触发这个实例new
    /// 然后解析所有打了Aspect标签的class进行解析打了有继承AspectInvokeAttribute的所有方法并且缓存起来
    /// </summary>
    [Component(AutofacScope = AutofacScope.SingleInstance,AutoActivate = true)]
    public class AopMethodInvokeCache
    {

        /// <summary>
        /// 构造方法
        /// </summary>
        public AopMethodInvokeCache(IComponentContext context)
        {
            CacheList = new ConcurrentDictionary<MethodInfo, List<AspectInvokeAttribute>>();
            DynamicCacheList = new ConcurrentDictionary<string, List<AspectInvokeAttribute>>();
            var componentModelCacheSingleton = context.Resolve<ComponentModelCacheSingleton>();
            var aspectClassList = componentModelCacheSingleton.ComponentModelCache.Values
                .Where(r => r.AspectAttribute != null).ToList();
            foreach (var aspectClass in aspectClassList)
            {
                var allAttributesinClass = aspectClass.CurrentType.GetReflector()
                    .GetCustomAttributes(typeof(AspectInvokeAttribute)).OfType<AspectInvokeAttribute>()
                    .Select(r => new {IsClass = true, Attribute = r, Index = r.OrderIndex}).ToList();

                var myArrayMethodInfo = aspectClass.CurrentType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName);

                foreach (var method in myArrayMethodInfo)
                {
                    var allAttributes = allAttributesinClass.Concat(method.GetReflector()
                            .GetCustomAttributes(typeof(AspectInvokeAttribute)).OfType<AspectInvokeAttribute>()
                            .Select(r => new { IsClass = false, Attribute = r, Index = r.OrderIndex }));

                    var attributes = allAttributes
                        .OrderBy(r => r.IsClass).ThenByDescending(r => r.Index)
                        .GroupBy(r => r.Attribute.GetType().FullName)
                        .Select(r => r.First().Attribute).ToList();

                    if (aspectClass.isDynamicGeneric)
                    {
                        DynamicCacheList.TryAdd(method.GetMethodInfoUniqueName(), attributes);
                        continue;
                    }
                    CacheList.TryAdd(method, attributes);
                }
            }
        }
        /// <summary>
        /// 缓存
        /// </summary>
        public ConcurrentDictionary<MethodInfo,List<AspectInvokeAttribute>> CacheList { get; set; }
        
        /// <summary>
        /// 由于动态泛型的method是跟着泛型T变化的  所以需要单独缓存
        /// </summary>
        public ConcurrentDictionary<string,List<AspectInvokeAttribute>> DynamicCacheList { get; set; }
        
     
    }


    /// <summary>
    /// AOP拦截器 配合打了 Aspect标签的class 和 里面打了 继承AspectInvokeAttribute 标签的 方法
    /// </summary>
    [Component(typeof(AopIntercept))]
    public class AopIntercept : AsyncInterceptor
    {
        private readonly IComponentContext _component;
        private readonly AopMethodInvokeCache _cache;


        /// <summary>
        /// 构造方法
        /// </summary>
        public AopIntercept(IComponentContext context, AopMethodInvokeCache cache)
        {
            _component = context;
            _cache = cache;
        }

        /// <summary>
        /// 执行前置拦截器
        /// </summary>
        /// <param name="invocation"></param>
        private async Task<Tuple<List<AspectPointAttribute>, List<AspectInvokeAttribute>,Exception>> BeforeInterceptAttribute(IInvocation invocation)
        {
            //先从缓存里面拿到这个方法时候打了继承AspectInvokeAttribute的标签
            if(!_cache.CacheList.TryGetValue(invocation.MethodInvocationTarget,out var Attributes) || Attributes==null || !Attributes.Any())
            {
                //动态泛型类
                if (!invocation.MethodInvocationTarget.DeclaringType.GetTypeInfo().IsGenericType || (!_cache.DynamicCacheList.TryGetValue(invocation.MethodInvocationTarget.GetMethodInfoUniqueName(), out var AttributesDynamic) || AttributesDynamic==null || !AttributesDynamic.Any()))
                {
                    return null;
                }

                Attributes = AttributesDynamic;
            }
           
            var aspectContext = new AspectContext(_component, invocation);
            Exception ex = null;
            try
            {
                foreach (var attribute in Attributes)
                {
                    //如果一个方法上面既有AspectAroundAttribute 又有 AspectBeforeAttribute 的话 按照下面的优先级 抛弃 AspectBeforeAttribute
                    switch (attribute)
                    {
                        case AspectAroundAttribute aspectAroundAttribute:
                            await aspectAroundAttribute.Before(aspectContext);
                            break;
                        case AspectBeforeAttribute aspectBeforeAttribute:
                            await aspectBeforeAttribute.Before(aspectContext);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                ex = e;
            }
            return new Tuple<List<AspectPointAttribute>, List<AspectInvokeAttribute>,Exception>(Attributes.OfType<AspectPointAttribute>().ToList(), Attributes,ex);
        }

        private async Task AfterInterceptAttribute(List<AspectInvokeAttribute> Attributes, IInvocation invocation, Exception exp)
        {
            var aspectContext = new AspectContext(_component, invocation, exp);
            foreach (var attribute in Attributes)
            {
                switch (attribute)
                {
                    case AspectAroundAttribute aspectAroundAttribute:
                        await aspectAroundAttribute.After(aspectContext);
                        break;
                    case AspectAfterAttribute aspectAfterAttribute:
                        await aspectAfterAttribute.After(aspectContext);
                        break;
                }
            }
        }

        /// <summary>
        /// 无返回值拦截器
        /// </summary>
        /// <param name="invocation"></param>
        /// <param name="proceedInfo"></param>
        /// <param name="proceed"></param>
        /// <returns></returns>
        protected override async Task InterceptAsync(IInvocation invocation, IInvocationProceedInfo proceedInfo, Func<IInvocation, IInvocationProceedInfo, Task> proceed)
        {
            var attribute = await BeforeInterceptAttribute(invocation);
            try
            {
                if (attribute == null)
                {
                    await proceed(invocation, proceedInfo);
                    return;
                }

                if (attribute.Item3 != null)
                {
                    await AfterInterceptAttribute(attribute.Item2, invocation, attribute.Item3);
                    throw attribute.Item3;
                }
                
                if (attribute.Item1 == null || !attribute.Item1.Any())
                {
                    await proceed(invocation, proceedInfo);
                }
                else
                {
                    AspectMiddlewareBuilder builder = new AspectMiddlewareBuilder();
                    foreach (var pointAspect in attribute.Item1)
                    {
                        builder.Use(next => async ctx => { await pointAspect.OnInvocation(ctx, next); });
                    }

                    builder.Use(next => async ctx => { await proceed(invocation, proceedInfo); });

                    var aspectfunc = builder.Build();
                    await aspectfunc(new AspectContext(_component, invocation));
                }
                await AfterInterceptAttribute(attribute.Item2, invocation, null);
            }
            catch (Exception e)
            {
                if(attribute!=null) await AfterInterceptAttribute(attribute.Item2, invocation, e);
                throw;
            }
        }

        /// <summary>
        /// 有返回值拦截器
        /// </summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="invocation"></param>
        /// <param name="proceedInfo"></param>
        /// <param name="proceed"></param>
        /// <returns></returns>
        protected override async Task<TResult> InterceptAsync<TResult>(IInvocation invocation, IInvocationProceedInfo proceedInfo, Func<IInvocation, IInvocationProceedInfo, Task<TResult>> proceed)
        {
            var attribute = await BeforeInterceptAttribute(invocation);
            try
            {
                TResult r;

                if (attribute == null)
                {
                    r = await proceed(invocation, proceedInfo);
                    return r;
                }

                if (attribute.Item3 != null)
                {
                    await AfterInterceptAttribute(attribute.Item2, invocation, attribute.Item3);
                    throw attribute.Item3;
                }
                
             
                if (attribute.Item1 == null || !attribute.Item1.Any())
                {
                    r = await proceed(invocation, proceedInfo);
                }
                else
                {
                    AspectMiddlewareBuilder builder = new AspectMiddlewareBuilder();
                    foreach (var pointAspect in attribute.Item1)
                    {
                        builder.Use(next => async ctx =>
                        {
                            await pointAspect.OnInvocation(ctx, next);
                            //如果有拦截器设置 ReturnValue 那么就直接拿这个作为整个拦截器的方法返回值
                            if (ctx.InvocationContext.ReturnValue != null)
                            {
                                ctx.Result = ctx.InvocationContext.ReturnValue;
                            }
                        });
                    }


                    builder.Use(next => async ctx => 
                    {
                         ctx.Result = await proceed(invocation, proceedInfo);
                         invocation.ReturnValue = ctx.Result;//原方法的执行返回值
                    });

                    var aspectfunc = builder.Build();
                    var aspectContext = new AspectContext(_component, invocation);
                    await aspectfunc(aspectContext);
                    r = (TResult)aspectContext.Result;
                }

                await AfterInterceptAttribute(attribute.Item2, invocation, null);
                return r;
            }
            catch (Exception e)
            {
                if(attribute!=null) await AfterInterceptAttribute(attribute.Item2, invocation, e);
                throw;
            }
        }

   
    }


    /// <summary>
    /// AOP Pointcut拦截器
    /// </summary>
    [Component(typeof(AspectJIntercept))]
    public class AspectJIntercept : AsyncInterceptor
    {
        private readonly IComponentContext _component;
        private readonly PointCutConfigurationList _configuration;


        /// <summary>
        /// 构造方法
        /// </summary>
        public AspectJIntercept(IComponentContext context, PointCutConfigurationList configurationList)
        {
            _component = context;
            _configuration = configurationList;
        }
        
        /// <summary>
        /// 一个目标方法只会适 Before After Arround 其中的一个切面
        /// </summary>
        /// <param name="invocation"></param>
        /// <param name="proceedInfo"></param>
        /// <param name="proceed"></param>
        /// <returns></returns>
        protected override async Task InterceptAsync(IInvocation invocation, IInvocationProceedInfo proceedInfo, Func<IInvocation, IInvocationProceedInfo, Task> proceed)
        {
            if (!_configuration.PointcutTargetInfoList.TryGetValue(invocation.MethodInvocationTarget, out var pointCut))
            {
                if (!invocation.MethodInvocationTarget.DeclaringType.GetTypeInfo().IsGenericType || !_configuration.DynamicPointcutTargetInfoList.TryGetValue(invocation.MethodInvocationTarget.GetMethodInfoUniqueName(), out var pointCutDynamic))
                {
                    //该方法不需要拦截
                    await proceed(invocation, proceedInfo);
                    return;
                }

                pointCut = pointCutDynamic;
            }

            //pointcut定义所在对象
            var instance = _component.Resolve(pointCut.PointClass);

            PointcutContext aspectContext = new PointcutContext
            {
                ComponentContext = _component,
                InvocationMethod = invocation.MethodInvocationTarget,
            };
                
            if (pointCut.AroundMethod != null)
            {
                aspectContext.Proceed = async () =>
                {
                    await proceed(invocation, proceedInfo);
                };
                
                var rt = AutoConfigurationHelper.InvokeInstanceMethod(instance, pointCut.AroundMethod, _component,aspectContext);
                if (typeof(Task).IsAssignableFrom(pointCut.AroundMethod.ReturnType))
                {
                    await ((Task) rt).ConfigureAwait(false);
                }
                return;
            }
            
            try
            {
                if (pointCut.BeforeMethod != null)
                {
                    var rtBefore = AutoConfigurationHelper.InvokeInstanceMethod(instance, pointCut.BeforeMethod, _component,aspectContext);
                    if (typeof(Task).IsAssignableFrom(pointCut.BeforeMethod.ReturnType))
                    {
                        await ((Task) rtBefore).ConfigureAwait(false);
                    }
                }
                await proceed(invocation, proceedInfo);
                if (pointCut.AfterMethod != null)
                {
                    var rtAfter = AutoConfigurationHelper.InvokeInstanceMethod(instance, pointCut.AfterMethod, _component,aspectContext);
                    if (typeof(Task).IsAssignableFrom(pointCut.AfterMethod.ReturnType))
                    {
                        await ((Task) rtAfter).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                aspectContext.Exception = e;
                if (pointCut.AfterMethod != null)
                {
                    var rtAfter = AutoConfigurationHelper.InvokeInstanceMethod(instance, pointCut.AfterMethod, _component,aspectContext);
                    if (typeof(Task).IsAssignableFrom(pointCut.AfterMethod.ReturnType))
                    {
                        await ((Task) rtAfter).ConfigureAwait(false);
                    }
                }
                throw;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="invocation"></param>
        /// <param name="proceedInfo"></param>
        /// <param name="proceed"></param>
        /// <typeparam name="TResult"></typeparam>
        /// <returns></returns>
        protected override async Task<TResult> InterceptAsync<TResult>(IInvocation invocation, IInvocationProceedInfo proceedInfo, Func<IInvocation, IInvocationProceedInfo, Task<TResult>> proceed)
        {
            if (!_configuration.PointcutTargetInfoList.TryGetValue(invocation.MethodInvocationTarget, out var pointCut))
            {
                if (!invocation.MethodInvocationTarget.DeclaringType.GetTypeInfo().IsGenericType || !_configuration.DynamicPointcutTargetInfoList.TryGetValue(invocation.MethodInvocationTarget.GetMethodInfoUniqueName(), out var pointCutDynamic))
                {
                    //该方法不需要拦截
                    return await proceed(invocation, proceedInfo);
                }
             
                pointCut = pointCutDynamic;
            }
            
            //pointcut定义所在对象
            var instance = _component.Resolve(pointCut.PointClass);

            PointcutContext aspectContext = new PointcutContext
            {
                ComponentContext = _component,
                InvocationMethod = invocation.MethodInvocationTarget,
            };
                
            if (pointCut.AroundMethod != null)
            {
                aspectContext.Proceed = async () =>
                {
                    invocation.ReturnValue =  await proceed(invocation, proceedInfo);
                };
                
                var rt = AutoConfigurationHelper.InvokeInstanceMethod(instance, pointCut.AroundMethod, _component,aspectContext);
                if (typeof(Task).IsAssignableFrom(pointCut.AroundMethod.ReturnType))
                {
                    await ((Task) rt).ConfigureAwait(false);
                }

                return (TResult)invocation.ReturnValue;
            }
            
            try
            {
                if (pointCut.BeforeMethod != null)
                {
                    var rtBefore = AutoConfigurationHelper.InvokeInstanceMethod(instance, pointCut.BeforeMethod, _component,aspectContext);
                    if (typeof(Task).IsAssignableFrom(pointCut.BeforeMethod.ReturnType))
                    {
                        await ((Task) rtBefore).ConfigureAwait(false);
                    }
                }
                
                var rt = await proceed(invocation, proceedInfo);
                
                
                if (pointCut.AfterMethod != null)
                {
                   var rtAfter = AutoConfigurationHelper.InvokeInstanceMethod(instance, pointCut.AfterMethod, _component,aspectContext);
                   if (typeof(Task).IsAssignableFrom(pointCut.AfterMethod.ReturnType))
                   {
                       await ((Task) rtAfter).ConfigureAwait(false);
                   }
                }

                return rt;
            }
            catch (Exception e)
            {
                aspectContext.Exception = e;
                if (pointCut.AfterMethod != null)
                {
                    var rtAfter = AutoConfigurationHelper.InvokeInstanceMethod(instance, pointCut.AfterMethod, _component,aspectContext);
                    if (typeof(Task).IsAssignableFrom(pointCut.AfterMethod.ReturnType))
                    {
                        await ((Task) rtAfter).ConfigureAwait(false);
                    }
                }
                throw;
            }
            
        }
    }
}
