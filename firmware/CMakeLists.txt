cmake_minimum_required(VERSION 3.16)
# One version for the whole repository: the VERSION file at the root (see CHANGELOG.md).
file(STRINGS "${CMAKE_CURRENT_LIST_DIR}/../VERSION" PROJECT_VER LIMIT_COUNT 1)
execute_process(COMMAND git describe --always --dirty --abbrev=7 --exclude *
                WORKING_DIRECTORY "${CMAKE_CURRENT_LIST_DIR}" OUTPUT_VARIABLE GL_BUILD
                OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
if(NOT GL_BUILD)
    set(GL_BUILD "nogit")
endif()
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${CMAKE_CURRENT_LIST_DIR}/../VERSION")

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(glasslink_du)
