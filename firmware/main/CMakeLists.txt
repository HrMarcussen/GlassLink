idf_component_register(
    SRCS "main.c" "usb_link.c" "display.c" "ident.c" "screens.c"
    INCLUDE_DIRS "."
    EMBED_FILES "fonts/inter-regular.ttf" "fonts/inter-semibold.ttf" "fonts/inter-bold.ttf"
    REQUIRES driver esp_lcd nvs_flash app_update esp_timer esp_driver_jpeg esp_driver_ppa esp_mm hal soc
)
target_compile_definitions(${COMPONENT_LIB} PRIVATE FW_VERSION="${PROJECT_VER}" FW_BUILD="${GL_BUILD}")
# Dimming in the decoder's colour conversion (display.c): our wrapper runs after the 2D-DMA driver sets the matrix.
target_link_libraries(${COMPONENT_LIB} INTERFACE "-Wl,--wrap=dma2d_configure_color_space_conversion")
