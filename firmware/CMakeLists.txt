cmake_minimum_required(VERSION 3.16)
# One version for the whole repository: the VERSION file at the root (see CHANGELOG.md).
file(STRINGS "${CMAKE_CURRENT_LIST_DIR}/../VERSION" PROJECT_VER LIMIT_COUNT 1)
execute_process(COMMAND git describe --always --dirty --abbrev=7 --exclude *
                WORKING_DIRECTORY "${CMAKE_CURRENT_LIST_DIR}" OUTPUT_VARIABLE GL_BUILD
                OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
if(NOT GL_BUILD)
    set(GL_BUILD "nogit")
endif()
# Re-run this configure step (and with it the build id) whenever the version, the checked-out commit or the staged
# state changes; otherwise a new commit would still build with the previous id.
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
             "${CMAKE_CURRENT_LIST_DIR}/../VERSION"
             "${CMAKE_CURRENT_LIST_DIR}/../.git/HEAD"
             "${CMAKE_CURRENT_LIST_DIR}/../.git/index")

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
# The vendor endpoint receives in transfers of CONFIG_TINYUSB_VENDOR_EPSIZE (16 KB) instead of one per 512-byte
# packet, which cost a pass through the TinyUSB task each (~6.7 MB/s). A transfer then ends on a short packet only, so
# the host must end a write of n x 512 bytes with a zero-length packet: both DMCs do (docs/usb-protocol.md).
idf_build_set_property(COMPILE_DEFINITIONS "CFG_TUD_VENDOR_RX_NEED_ZLP=1" APPEND)
# Receive transfers are started by usb_link.c only, once the previous one is in its buffer (#61)
idf_build_set_property(COMPILE_DEFINITIONS "CFG_TUD_VENDOR_RX_MANUAL_XFER=1" APPEND)
project(glasslink_du)
