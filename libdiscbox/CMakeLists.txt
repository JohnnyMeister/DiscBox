cmake_minimum_required(VERSION 3.16)
project(discbox VERSION 0.1.0 LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# ── Options ────────────────────────────────────────────────────
option(DISCBOX_BUILD_TESTS    "Build the test suite"    ON)
option(DISCBOX_BUILD_EXAMPLES "Build example programs"  ON)
option(DISCBOX_DEBUG          "Enable debug logging"    OFF)

# ── Find dependencies ──────────────────────────────────────────
find_package(CURL REQUIRED)

# ── Library sources ────────────────────────────────────────────
set(DISCBOX_SOURCES
    src/discbox.c
    src/chunk.c
    src/discord.c
    src/db.c
    src/ratelimit.c
)

# ── Build as shared + static ───────────────────────────────────
add_library(discbox_shared SHARED ${DISCBOX_SOURCES})
add_library(discbox_static STATIC ${DISCBOX_SOURCES})

# Alias so consumers can use target_link_libraries(... discbox::shared)
add_library(discbox::shared ALIAS discbox_shared)
add_library(discbox::static ALIAS discbox_static)

foreach(target discbox_shared discbox_static)
    target_include_directories(${target}
        PUBLIC  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
                $<INSTALL_INTERFACE:include>
        PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/vendor
                ${CMAKE_CURRENT_SOURCE_DIR}/src
    )
    target_link_libraries(${target}
        PRIVATE CURL::libcurl sqlite3
    )
    if(DISCBOX_DEBUG)
        target_compile_definitions(${target} PRIVATE DISCBOX_DEBUG=1)
    endif()
    # Warnings
    target_compile_options(${target} PRIVATE
        $<$<C_COMPILER_ID:GNU,Clang>:-Wall -Wextra -Wpedantic>
        $<$<C_COMPILER_ID:MSVC>:/W4>
    )
endforeach()

set_target_properties(discbox_shared PROPERTIES
    OUTPUT_NAME discbox
    VERSION     ${PROJECT_VERSION}
    SOVERSION   0
)
set_target_properties(discbox_static PROPERTIES OUTPUT_NAME discbox_static)

# ── Tests ──────────────────────────────────────────────────────
if(DISCBOX_BUILD_TESTS)
    enable_testing()

    # Test: chunk split/merge
    add_executable(test_chunk tests/test_chunk.c)
    target_link_libraries(test_chunk PRIVATE discbox::static)
    target_include_directories(test_chunk PRIVATE src vendor)
    add_test(NAME chunk COMMAND test_chunk)

    # Test: database operations
    add_executable(test_db tests/test_db.c)
    target_link_libraries(test_db PRIVATE discbox::static)
    target_include_directories(test_db PRIVATE src vendor)
    add_test(NAME db COMMAND test_db)
endif()

# ── Examples ───────────────────────────────────────────────────
if(DISCBOX_BUILD_EXAMPLES)
    add_executable(discbox_cli examples/cli.c)
    target_link_libraries(discbox_cli PRIVATE discbox::static)
endif()

# ── Install ────────────────────────────────────────────────────
include(GNUInstallDirs)
install(TARGETS discbox_shared discbox_static
        EXPORT  discbox-targets
        LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
        ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
)
install(FILES include/discbox.h DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
