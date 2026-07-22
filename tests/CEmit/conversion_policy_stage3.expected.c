// file: conversion_policy_stage3.c
#include "conversion_policy_stage3_private.h"


void policy(unsigned char * __near nearBytes, unsigned char * __far farBytes, void (* __near nearFn)(void), int nearIndex __near, unsigned char * __far nearItems, unsigned int nearItems_length)
{
	unsigned char * __far widenedBytes = nearBytes;
	unsigned char * __near narrowedBytes = (unsigned char * __near)(farBytes);
	void (* __far widenedFn)(void) = (void (* __far)(void))(nearFn);
	int widenedIndex __far = nearIndex;
	int narrowedIndex __near = (int  __near)(widenedIndex);
	unsigned char * __far widenedItems = nearItems;
	unsigned int widenedItems_length = nearItems_length;
}

// file: conversion_policy_stage3.h
#ifndef CONVERSION_POLICY_STAGE3_H_
#define CONVERSION_POLICY_STAGE3_H_

#include "conversion_policy_stage3_private.h"

void policy(unsigned char * __near nearBytes, unsigned char * __far farBytes, void (* __near nearFn)(void), int nearIndex __near, unsigned char * __far nearItems, unsigned int nearItems_length);

#endif
// file: conversion_policy_stage3_private.h
#ifndef CONVERSION_POLICY_STAGE3_PRIVATE_H_
#define CONVERSION_POLICY_STAGE3_PRIVATE_H_

#include <stddef.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
void policy(unsigned char * __near nearBytes, unsigned char * __far farBytes, void (* __near nearFn)(void), int nearIndex __near, unsigned char * __far nearItems, unsigned int nearItems_length);

/* Object declarations. */


#endif
