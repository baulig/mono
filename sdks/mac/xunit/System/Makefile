SYSTEM_DIR = ../../../../mcs/class/System
XUNIT_TEST_LIB = $(SYSTEM_DIR)/xammac_System_xunit-test.dll

MAC_BUILD_DIR = ../../../../../../_mac-build/Library/Frameworks/Xamarin.Mac.framework/Versions/git
OUT_DIR = ./bin/Debug

OUT_BINARY = $(OUT_DIR)/MacTest.app/Contents/MacOS/MacTest

XUNIT_TEST_SOURCES = $(SYSTEM_DIR)/xammac_System_xtest.dll.sources $(SYSTEM_DIR)/appletls-xtest.sources $(SYSTEM_DIR)/martin-mac.sources

all:	MacTest.csproj $(XUNIT_TEST_LIB)

MacTest.csproj: ../../../ios/xunit/generator.pl MacTest.template MacTest.template $(XUNIT_TEST_SOURCES)
	-rm -f $@ output-tmp
	../../../ios/xunit/generator.pl MacTest.template /dev/null output-tmp && mv output-tmp $@

clean:
	-rm -rf bin obj log* *.log* MacTest.csproj $(XUNIT_TEST_LIB)

$(XUNIT_TEST_LIB): $(XUNIT_TEST_SOURCES) Makefile $(SYSTEM_DIR)/Mono/MartinXTest.cs
	touch $(SYSTEM_DIR)/xammac_System_xtest.dll.sources && $(MAKE) -C $(SYSTEM_DIR) PROFILE=xammac xunit-test

$(OUT_BINARY): MacTest.csproj Makefile $(XUNIT_TEST_LIB)
	msbuild
	cp $(MAC_BUILD_DIR)/lib/libmono-apple-crypto.* $(OUT_DIR)/MacTest.app/Contents/MonoBundle/

build: MacTest.csproj $(XUNIT_TEST_LIB)

run: MacTest.csproj $(XUNIT_TEST_LIB)
	msbuild
	cp $(MAC_BUILD_DIR)/lib/libmono-apple-crypto.* $(OUT_DIR)/MacTest.app/Contents/MonoBundle/
	./launch.sh
