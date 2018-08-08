#!/bin/sh
cp /Developer/Work/xamarin-macios/external/mono/external/xunit-binaries/xunit.execution.dotnet.dll ./bin/iPhoneSimulator/Debug/PhoneTest.app
cp /Developer/Work/xamarin-macios/_ios-build//Library/Frameworks/Xamarin.iOS.framework/Versions/git/SDKs/MonoTouch.iphonesimulator.sdk/usr/lib/libmono-apple-crypto.*.dylib ./bin/iPhoneSimulator/Debug/PhoneTest.app
/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/bin/mlaunch -v --device=:v2:runtime=com.apple.CoreSimulator.SimRuntime.iOS-11-4,devicetype=com.apple.CoreSimulator.SimDeviceType.iPhone-X --install-progress --installsim=./bin/iPhoneSimulator/Debug/PhoneTest.app
/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/bin/mlaunch -v -v -v --device=:v2:runtime=com.apple.CoreSimulator.SimRuntime.iOS-11-4,devicetype=com.apple.CoreSimulator.SimDeviceType.iPhone-X --launchsim=./bin/iPhoneSimulator/Debug/PhoneTest.app --stdout=simulator.log --stderr=simulator.log --wait-for-exit
