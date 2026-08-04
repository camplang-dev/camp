// file: namespace_symbol_defaults.c
#include "namespace_symbol_defaults_private.h"


int PixelImageBuffer_getWidth(const PixelImageBuffer *this)
{
	return 4;
}

int PixelImageBuffer_CreateDefault(void)
{
	return 7;
}

int PixelImageMetrics_getDefault(void)
{
	return PixelImageMetrics_Base;
}

int PixelImage_CreateBuffer(void)
{
	PixelImageBuffer buffer = (PixelImageBuffer){ 0 };
	return ((PixelImageBuffer_getWidth(&buffer) + PixelImageBuffer_CreateDefault()) + PixelImageMetrics_getDefault());
}

// file: namespace_symbol_defaults.h
#ifndef NAMESPACE_SYMBOL_DEFAULTS_H_
#define NAMESPACE_SYMBOL_DEFAULTS_H_

#include "namespace_symbol_defaults_private.h"

int PixelImage_nativeCall(void);
int nativeExact(void);
int PixelImage_CreateBuffer(void);

#endif
// file: namespace_symbol_defaults_private.h
#ifndef NAMESPACE_SYMBOL_DEFAULTS_PRIVATE_H_
#define NAMESPACE_SYMBOL_DEFAULTS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct PixelImageBuffer PixelImageBuffer;
typedef struct PixelImage_buffer PixelImage_buffer;

/* Enums. */

/* Newtypes. */

/* Constants. */
#define PixelImageMetrics_Base ((int)3)

/* Layouts. */
struct PixelImageBuffer
{
	char _camp_empty;
};
struct PixelImage_buffer
{
	char _camp_empty;
};

/* Function declarations. */
int PixelImage_nativeCall(void);
int nativeExact(void);
int PixelImageBuffer_getWidth(const PixelImageBuffer *this);
int PixelImageBuffer_CreateDefault(void);
int PixelImageMetrics_getDefault(void);
int PixelImage_CreateBuffer(void);

/* Object declarations. */


#endif
