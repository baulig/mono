#!/usr/bin/perl -w
use strict;

my $ROOT = "/Developer/Work/xamarin-macios";
my $MONO_ROOT = "$ROOT/external/mono";
my $COREFX_ROOT = "$MONO_ROOT/external/corefx/src";
my $XUNIT_BINARIES = "$MONO_ROOT/external/xunit-binaries";
my $INDENT_PREFIX = "    ";
my $INDENT = "  ";

my $template = $ARGV[0] or die "missing template arg";
my $sources = $ARGV[1] or die "missing source arg";
my $output = $ARGV[2] or die "missing output arg";

open TEMPLATE, $template or die "can't find template file: $!";
open OUTPUT, "> $output" or die "can't open output file: $!";

sub replaceLink
{
    my ($source,$pattern,$replacement) = @_;

    if ($source =~ m,^\@$pattern\@/(.*),) {
        my ($file, $fullPath) = ($1, "$replacement/$1");
        $file =~ s,/,\\,g;
        $fullPath =~ s,/,\\,g;

        print OUTPUT "$INDENT_PREFIX<Compile Include=\"$fullPath\">\n$INDENT_PREFIX$INDENT<Link>$file</Link>\n$INDENT_PREFIX</Compile>\n";
        return 1;
    }
}

sub includeFile
{
    my ($source) = @_;

    $source =~ s,/,\\,g;
    print OUTPUT "$INDENT_PREFIX<Compile Include=\"$source\" />\n";
}

sub parseSources
{
    open SOURCES, $sources or die "can't find sources file: $!";

    while (my $line = <SOURCES>) {
        $line =~ s/\R\z//g;

        $line =~ s/^\s*//g;
        $line =~ s/\s*$//g;

        next if $line =~ /^#/;
        next if $line =~ /^$/;

        next if replaceLink ($line, "COREFX_DIR", $COREFX_ROOT);
        next if replaceLink ($line, "MONO_DIR", $MONO_ROOT);
        includeFile ($line);
    }
    close SOURCES;
}

sub parseTemplate
{
    my ($winRoot, $winCoreFxRoot, $winMonoRoot, $winXunit) = ($ROOT, $COREFX_ROOT, $MONO_ROOT, $XUNIT_BINARIES);
    $winRoot =~ s,/,\\,g;
    $winCoreFxRoot =~ s,/,\\,g;
    $winMonoRoot =~ s,/,\\,g;
    $winXunit =~ s,/,\\,g;

    while (my $line = <TEMPLATE>) {
        if ($line =~ m,^\s*\@SOURCE_LIST\@,) {
            parseSources ();
            next;
        }

        $line =~ s/\@ROOT\@/$winRoot/g;
        $line =~ s/\@COREFX_DIR\@/$winCoreFxRoot/g;
        $line =~ s/\@MONO_DIR\@/$winMonoRoot/g;
        $line =~ s/\@XUNIT_BINARIES\@/$winXunit/g;
        print OUTPUT $line;
    }
}

parseTemplate()


