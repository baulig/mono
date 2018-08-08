SYSTEM_DIR = ../../../../mcs/class/System
XUNIT_TEST_LIB = $(SYSTEM_DIR)/monotouch_System_xunit-test.dll

XUNIT_TEST_SOURCES = $(SYSTEM_DIR)/monotouch_System_xtest.dll.sources $(SYSTEM_DIR)/martin-xtest.sources $(SYSTEM_DIR)/martin-phone.sources

all:	PhoneTest.csproj $(XUNIT_TEST_LIB)

PhoneTest.csproj: ../generator.pl PhoneTest.template martin-phone.sources PhoneTest.template
	-rm -f $@ output-tmp
	../generator.pl PhoneTest.template martin-phone.sources output-tmp && mv output-tmp $@

clean:
	-rm -rf bin obj log* *.log* MacTest.csproj $(XUNIT_TEST_LIB)

$(XUNIT_TEST_LIB): Makefile $(XUNIT_TEST_SOURCES) PhoneTest.csproj
	touch $(SYSTEM_DIR)/monotouch_System_xtest.dll.sources && $(MAKE) -C $(SYSTEM_DIR) PROFILE=monotouch xunit-test

build: PhoneTest.csproj $(XUNIT_TEST_LIB)
	msbuild

run: PhoneTest.csproj $(XUNIT_TEST_LIB)
	msbuild
	./launch.sh

