// file: conversion_callable_stage4.c
#include "conversion_callable_stage4_private.h"


void targetSpecs(void (* __near nearRaw)(void), int (__pascal * __near nearFunction)(void))
{
	void (* __far farRaw)(void) = (void (* __far)(void))(nearRaw);
	int (__pascal * __near sameFunction)(void) = nearFunction;
}

// file: conversion_callable_stage4.h
#ifndef CONVERSION_CALLABLE_STAGE4_H_
#define CONVERSION_CALLABLE_STAGE4_H_

#include "conversion_callable_stage4_private.h"

void targetSpecs(void (* __near nearRaw)(void), int (__pascal * __near nearFunction)(void));

#endif
// file: conversion_callable_stage4_private.h
#ifndef CONVERSION_CALLABLE_STAGE4_PRIVATE_H_
#define CONVERSION_CALLABLE_STAGE4_PRIVATE_H_

#include <stddef.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
void targetSpecs(void (* __near nearRaw)(void), int (__pascal * __near nearFunction)(void));

/* Object declarations. */


#endif
